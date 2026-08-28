using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.DbMetadata;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services.Chat;

public sealed class DbChatService(
    ILlmClient ollamaClient,
    TableResultFormatterService tableResultFormatter,
    MessageAnalyzeService messageAnalyzeService,
    SqlApprovalService sqlApprovalService,
    IOptions<AppConfig> appConfig,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<OpenRouterOptions> openRouterOptions,
    ToolCallingResolverService toolCallingResolver,
    DatabaseContextService databaseContextService,
    ILogger<ValidatedSqlExecutor> sqlLogger)
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
            var input = await output.ReadUserInput("Enter request:");
            if (input is null)
            {
                return;
            }

            var userRequest = input.Trim();

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
        DatabaseContext databaseContext;
        try
        {
            databaseContext = await databaseContextService.GetAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            output.OutError($"Could not initialize database context: {ex.Message}");
            return null;
        }

        var databaseName = databaseContext.DatabaseName;
        var tables = databaseContext.PublicTables;
        var securityFilter = databaseContext.SecurityFilter;

        var userQuery = securityFilter.GetUsersQuery("UserId", "DisplayName");

        var userSelection = await TryGetSecIdentity(connectionString, output, cancellationToken, userQuery);
        if (userSelection.ExitRequested)
        {
            return null;
        }

        var userId = userSelection.UserId;

        var schemaPrompt = databaseContext.SchemaPrompt;
        var analyzerSchemaPrompt = databaseContext.AnalyzerSchemaPrompt;

        var messageAnalyzeSession = messageAnalyzeService.CreateSession(output, databaseName, tables, analyzerSchemaPrompt);
        var sqlApprovalSession = sqlApprovalService.CreateSession(output, databaseName, tables, schemaPrompt);
        var executor = new ValidatedSqlExecutor(
            output,
            appConfig,
            securityFilter,
            tableResultFormatter,
            sqlApprovalSession,
            userId,
            connectionString,
            DatabaseContextService.CreateDatabase,
            sqlLogger);

        var toolCalling = await toolCallingResolver.ResolveAsync(appConfig.ToolCalling, model, cancellationToken);

        return new DbChatSession(
            output,
            appConfig,
            ollamaClient: ollamaClient,
            messageAnalyzeSession: messageAnalyzeSession,
            validatedSqlExecutor: executor,
            schemaPrompt: schemaPrompt,
            llmName: model,
            databaseName: databaseName,
            useNativeTools: toolCalling.UseNativeTools
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

    private static async Task<UserSelection> TryGetSecIdentity(
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
                    var input = await output.ReadUserInput("Enter user id or /exit:");
                    if (input is null)
                    {
                        output.OutDataLine("Exiting.");
                        return new UserSelection(true, null);
                    }

                    var userInput = input.Trim();

                    if (string.Equals(userInput, "/exit", StringComparison.OrdinalIgnoreCase))
                    {
                        output.OutDataLine("Exiting.");
                        return new UserSelection(true, null);
                    }

                    if (int.TryParse(userInput, out var userId))
                    {
                        if (userId == 0)
                        {
                            output.OutDataLine("No user was selected. The app will show all available data.");
                            output.OutDataLine(string.Empty);
                            return new UserSelection(false, null);
                        }

                        if (!users.ContainsKey(userId))
                        {
                            output.OutErrorLine($"Could not find a user with id: {userId}");
                            continue;
                        }

                        output.OutDataLine($"User {userId} was selected. The app will now show only the data available to that user.");
                        output.OutDataLine(string.Empty);
                        return new UserSelection(false, userId);
                    }

                    output.OutErrorLine("Please enter a valid integer user id or /exit.");
                }
            }
            catch (Exception ex)
            {
                output.OutError($"Could not execute user query: {ex.Message}");
                return new UserSelection(false, null);
            }
        }

        return new UserSelection(false, null);
    }

    private static string GetConfiguredModel(AppConfig appConfig, OllamaOptions ollamaOptions, OpenRouterOptions openRouterOptions)
    {
        return string.Equals(appConfig.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase)
            ? openRouterOptions.Model
            : ollamaOptions.Model;
    }

    private readonly record struct UserSelection(bool ExitRequested, int? UserId);

}
