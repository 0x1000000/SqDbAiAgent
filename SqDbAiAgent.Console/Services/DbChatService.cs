using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Helpers;
using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.DbMetadata;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class DbChatService(
    ILlmClient ollamaClient,
    ITablePrinter tablePrinter,
    IAgentTableFormatter agentTableFormatter,
    IMessageAnalyzeService messageAnalyzeService,
    ISqlApprovalService sqlApprovalService,
    IOptions<AppConfig> appConfig,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<OpenRouterOptions> openRouterOptions,
    ISecurityFilterFactoryService securityFilterFactoryService)
    : IChatService
{
    private readonly AppConfig _appConfig = appConfig.Value;
    private readonly OllamaOptions _ollamaOptions = ollamaOptions.Value;
    private readonly OpenRouterOptions _openRouterOptions = openRouterOptions.Value;

    public async Task RunAsync(IConsoleOutput output, CancellationToken cancellationToken = default)
    {
        var dbChatSession = await InitDbChatSession(
            this._appConfig,
            this._appConfig.ConnectionString,
            GetConfiguredModel(this._appConfig, this._ollamaOptions, this._openRouterOptions),
            output,
            cancellationToken
        );

        if (dbChatSession == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var userRequest = (await output.ReadUserInput("Enter request:")).Trim();

            if (userRequest.ToLower() is "/exit" or "\\exit")
            {
                return;
            }

            if (!await dbChatSession.HandleInputAsync(userRequest, cancellationToken))
            {
                return;
            }
        }
    }

    private async Task<DbChatSession?> InitDbChatSession(
        AppConfig appConfig,
        string connectionString,
        string model,
        IConsoleOutput output,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TableBase> tables;
        try
        {
            tables = await GetTables(connectionString, cancellationToken);
        }
        catch (Exception)
        {
            output.OutError("Could not get tables");
            return null;
        }

        var databaseName = GetDatabaseName(connectionString);

        if(!securityFilterFactoryService.TryCreateSecurityFilter(databaseName, tables, out var securityFilter, out var error))
        {
            output.OutError($"Could not create security filter: {error}");
            return null;
        }

        tables = securityFilter.GetPublicTables();

        var userQuery = securityFilter.GetUsersQuery("UserId", "DisplayName");

        var userId = await TryGetSecIdentity(connectionString, output, cancellationToken, userQuery);

        var schemaPrompt = BuildSchemaPrompt(databaseName, tables);
        var analyzerSchemaPrompt = BuildAnalyzerSchemaPrompt(databaseName, tables);

        var messageAnalyzeSession = messageAnalyzeService.CreateSession(output, databaseName, tables, analyzerSchemaPrompt);
        var sqlApprovalSession = sqlApprovalService.CreateSession(output, databaseName, tables, schemaPrompt);

        return new DbChatSession(
            output,
            appConfig,
            securityFilter: securityFilter,
            ollamaClient: ollamaClient,
            tablePrinter: tablePrinter,
            agentTableFormatter: agentTableFormatter,
            messageAnalyzeSession: messageAnalyzeSession,
            sqlApprovalSession: sqlApprovalSession,
            userId,
            schemaPrompt: schemaPrompt,
            llmName: model,
            connectionString: connectionString,
            databaseName: databaseName,
            dbFactory: GetDb
        );
    }

    private static async Task<IReadOnlyList<SqTable>> GetTables(string connectionString, CancellationToken cancellationToken)
    {
        await using var db = GetDb(connectionString);
        return await db.GetTables(cancellationToken);
    }

    private static SqDatabase<SqlConnection> GetDb(string connectionString)
    {
        return new SqDatabase<SqlConnection>(
            new SqlConnection(connectionString),
            (connection, sql) => new SqlCommand(sql, connection),
            TSqlExporter.Default,
            ParametrizationMode.LiteralFallback,
            true);
    }

    private static string GetDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? "the configured"
            : builder.InitialCatalog;
    }

    private static async Task<int?> TryGetSecIdentity(
        string connectionString,
        IConsoleOutput output,
        CancellationToken cancellationToken,
        IExprReadOnlyQuery? userQuery)
    {
        if (userQuery != null)
        {
            try
            {
                await using var db = GetDb(connectionString);

                var users = await userQuery.Query(
                    db,
                    new SortedDictionary<int, string>(),
                    (acc, next) =>
                    {
                        acc.Add(next.GetInt32("UserId"), next.GetString("DisplayName"));
                        return acc;
                    },
                    cancellationToken
                );

                foreach (var kv in users)
                {
                    output.OutDataLine($"{kv.Key} - {kv.Value}");
                }

                output.OutDataLine(string.Empty);
                output.OutDataLine("Select a user id to establish the user security context for later data visibility filtering.");
                output.OutDataLine("Enter 0 to continue without selecting a user. In that case, all available data will remain visible.");
                output.OutDataLine(string.Empty);

                while (true)
                {
                    var userInput = (await output.ReadUserInput("Enter user id or /exit:")).Trim();

                    if (string.Equals(userInput, "/exit", StringComparison.OrdinalIgnoreCase))
                    {
                        output.OutDataLine("Exiting.");
                        return null;
                    }

                    if (int.TryParse(userInput, out var userId))
                    {
                        if (userId == 0)
                        {
                            output.OutDataLine("No user was selected. The app will show all available data.");
                            output.OutDataLine(string.Empty);
                            return null;
                        }

                        if (!users.ContainsKey(userId))
                        {
                            output.OutErrorLine($"Could not find a user with id: {userId}");
                            continue;
                        }

                        output.OutDataLine($"User {userId} was selected. The app will now show only the data available to that user.");
                        output.OutDataLine(string.Empty);
                        return userId;
                    }

                    output.OutErrorLine("Please enter a valid integer user id or /exit.");
                }
            }
            catch (Exception ex)
            {
                output.OutError($"Could not execute user query: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private static string BuildSchemaPrompt(
        string databaseName,
        IReadOnlyList<TableBase> publicTables)
    {
        using var memoryStream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(memoryStream))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("productInfo");
            writer.WriteString("name", databaseName);
            writer.WriteString("kind", "console proof of concept");
            writer.WriteString(
                "purpose",
                $"Convert user requests into validated read-only Microsoft SQL Server queries for the {databaseName} database."
            );
            writer.WriteString("llmRuntime", "Local Ollama llmName");
            writer.WriteString(
                "validation",
                "Generated SQL is parsed with SqTSqlParser, compared with the SqExpress table llmName, and retried on parser or SQL Server runtime errors."
            );
            writer.WriteString("currentRestriction", "The current workflow executes only read-only queries.");
            writer.WriteString(
                "sqExpressSummary",
                "SqExpress provides the table descriptors, SQL generation/export support, and parser-based validation used by the product."
            );
            writer.WriteString("sqExpressGitHub", "https://github.com/0x1000000/SqExpress");
            writer.WriteEndObject();

            writer.WriteStartArray("tables");
            foreach (var table in publicTables)
            {
                writer.WriteStartObject();
                writer.WriteString("tableName", table.FullName.ToSql(TSqlExporter.Default));
                writer.WriteStartArray("columns");
                foreach (var column in table.Columns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", column.ColumnName.ToSql(TSqlExporter.Default));
                    writer.WriteString("type", column.SqlType.ToSql(TSqlExporter.Default));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("relationships");
            foreach (var relationship in SqExpressHelpers.InferRelationships(publicTables))
            {
                writer.WriteStartObject();
                writer.WriteString("from", relationship.From);
                writer.WriteString("to", relationship.To);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("guidance");
            writer.WriteStringValue("Use only columns defined on the referenced table descriptors.");
            writer.WriteStringValue(
                "If a requested attribute is not on a table, follow llmName-defined foreign key relationships instead of inventing a direct column."
            );
            writer.WriteStringValue(
                "When filtering by a date that belongs to a parent entity, join to that parent table instead of assuming the child table has the same date column."
            );
            writer.WriteStringValue("Use explicit table aliases and qualify column references in multi-table queries.");
            writer.WriteStringValue(
                "Prefer straightforward joins and aggregates over advanced SQL constructs when a simpler equivalent exists."
            );
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private static string BuildAnalyzerSchemaPrompt(
        string databaseName,
        IReadOnlyList<TableBase> publicTables)
    {
        using var memoryStream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(memoryStream))
        {
            writer.WriteStartObject();

            writer.WriteString("databaseName", databaseName);

            writer.WriteStartArray("tables");
            foreach (var table in publicTables)
            {
                writer.WriteStringValue(table.FullName.ToSql(TSqlExporter.Default));
            }

            writer.WriteEndArray();

            writer.WriteStartArray("relationships");
            foreach (var relationship in SqExpressHelpers.InferRelationships(publicTables))
            {
                writer.WriteStartObject();
                writer.WriteString("from", relationship.From);
                writer.WriteString("to", relationship.To);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private static string GetConfiguredModel(AppConfig appConfig, OllamaOptions ollamaOptions, OpenRouterOptions openRouterOptions)
    {
        return string.Equals(appConfig.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase)
            ? openRouterOptions.Model
            : ollamaOptions.Model;
    }

}
