using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Helpers;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.DbMetadata;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services.Database;

public sealed class DatabaseContextService(
    IOptions<AppConfig> appConfig,
    SecurityFilterFactoryService securityFilterFactory)
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private DatabaseContext? _context;

    public async Task<DatabaseContext> GetAsync(CancellationToken cancellationToken = default)
    {
        if (this._context is not null)
        {
            return this._context;
        }

        await this._initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (this._context is not null)
            {
                return this._context;
            }

            var connectionString = appConfig.Value.ConnectionString;
            var databaseName = GetDatabaseName(connectionString);
            var tables = await GetTablesAsync(connectionString, cancellationToken);
            if (!securityFilterFactory.TryCreateSecurityFilter(
                    databaseName,
                    tables,
                    out var securityFilter,
                    out var error))
            {
                throw new InvalidOperationException($"Could not create security filter: {error}");
            }

            var publicTables = securityFilter.GetPublicTables();
            var securityUsersQuery = securityFilter.GetUsersQuery("UserId", "DisplayName");
            var users = await GetSecurityUsersAsync(
                connectionString,
                securityUsersQuery,
                cancellationToken);
            this._context = new DatabaseContext(
                databaseName,
                connectionString,
                publicTables,
                securityFilter,
                BuildSchemaPrompt(databaseName, publicTables),
                BuildAnalyzerSchemaPrompt(databaseName, publicTables),
                securityUsersQuery is not null,
                users);
            return this._context;
        }
        finally
        {
            this._initializationLock.Release();
        }
    }

    public static SqDatabase<SqlConnection> CreateDatabase(string connectionString) =>
        new(
            new SqlConnection(connectionString),
            (connection, sql) => new SqlCommand(sql, connection),
            TSqlExporter.Default,
            ParametrizationMode.LiteralFallback,
            true);

    private static async Task<IReadOnlyList<SqTable>> GetTablesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var db = CreateDatabase(connectionString);
        return await db.GetTables(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<int, string>> GetSecurityUsersAsync(
        string connectionString,
        IExprReadOnlyQuery? userQuery,
        CancellationToken cancellationToken)
    {
        if (userQuery is null)
        {
            return new Dictionary<int, string>();
        }

        await using var db = CreateDatabase(connectionString);
        return await userQuery.Query(
            db,
            new SortedDictionary<int, string>(),
            (users, record) =>
            {
                users.Add(record.GetInt32("UserId"), record.GetString("DisplayName"));
                return users;
            },
            cancellationToken);
    }

    private static string GetDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("The connection string must specify a database name.");
        }

        return builder.InitialCatalog;
    }

    internal static string BuildSchemaPrompt(string databaseName, IReadOnlyList<TableBase> publicTables)
    {
        using var memoryStream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(memoryStream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("context");
            writer.WriteString("databaseName", databaseName);
            writer.WriteString("kind", "connected database");
            writer.WriteString(
                "purpose",
                "Convert user requests into validated read-only Microsoft SQL Server queries for the connected database.");
            writer.WriteString(
                "validation",
                "SQL is parsed with SqTSqlParser, compared with the public SqExpress table model, restricted to read-only queries, and passed through user-security rewriting.");
            writer.WriteString("currentRestriction", "Only read-only queries are supported.");
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
            writer.WriteStringValue("Follow defined foreign-key relationships instead of inventing direct columns.");
            writer.WriteStringValue("Use explicit aliases and qualify columns in multi-table queries.");
            writer.WriteStringValue("Never query INFORMATION_SCHEMA, sys catalogs, system tables, or metadata functions.");
            writer.WriteStringValue("Assume end users do not know SQL or database internals; present answers in clear domain language.");
            writer.WriteStringValue("Unless explicitly requested, do not expose SQL text, schema/table/column identifiers, tool calls, or validation and security implementation details.");
            writer.WriteStringValue("Queries without an explicit outer TOP or OFFSET/FETCH limit receive the configured default row limit during validation; explicit outer limits are preserved.");
            writer.WriteStringValue("Entity/Attribute/Value (EAV) is a common relational pattern. Recognize possible entity-key, attribute-key, and value-table shapes only from exposed columns and relationships.");
            writer.WriteStringValue("Do not assume EAV is present, invent an EAV mapping, or sample arbitrary data to discover one.");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    internal static string BuildAnalyzerSchemaPrompt(string databaseName, IReadOnlyList<TableBase> publicTables)
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
}

public sealed record DatabaseContext(
    string DatabaseName,
    string ConnectionString,
    IReadOnlyList<TableBase> PublicTables,
    ISecurityFilter SecurityFilter,
    string SchemaPrompt,
    string AnalyzerSchemaPrompt,
    bool RequiresSecurityUser,
    IReadOnlyDictionary<int, string> SecurityUsers);
