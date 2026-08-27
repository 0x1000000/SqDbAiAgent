using Microsoft.Extensions.Options;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

public sealed class McpDatabaseService(
    DatabaseContextService databaseContextService,
    IOptions<AppConfig> appConfig,
    TableResultFormatterService tableResultFormatter,
    McpRuntimeContextService runtimeContext)
{
    public const string UserHeaderName = McpContractNames.DatabaseUserHeader;

    public async Task<IReadOnlyList<McpSecurityUser>> ListSecurityUsersAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await databaseContextService.GetAsync(cancellationToken);
        return context.SecurityUsers
            .Select(user => new McpSecurityUser(user.Key, user.Value))
            .ToArray();
    }

    public async Task<McpQueryResponse> SubmitAsync(
        string userRequest,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var context = await databaseContextService.GetAsync(cancellationToken);
        var securityUser = ResolveSecurityUser(
            runtimeContext.GetSecurityUserValue(),
            context,
            runtimeContext.AllowsUnfilteredSecurityContext);
        if (!securityUser.Success)
        {
            return McpQueryResponse.Failed(securityUser.Error!);
        }

        var executor = CreateExecutor(context, securityUser.UserId);
        var result = await executor.SubmitForAgentAsync(userRequest, sql, cancellationToken);
        return result is null
            ? McpQueryResponse.Failed(executor.LastFailure ?? "SQL validation or execution failed.")
            : BuildResponse(result);
    }

    public async Task<McpQueryResponse> InvestigateAsync(
        string userRequest,
        string purpose,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var context = await databaseContextService.GetAsync(cancellationToken);
        var securityUser = ResolveSecurityUser(
            runtimeContext.GetSecurityUserValue(),
            context,
            runtimeContext.AllowsUnfilteredSecurityContext);
        if (!securityUser.Success)
        {
            return McpQueryResponse.Failed(securityUser.Error!);
        }

        var executor = CreateExecutor(context, securityUser.UserId);
        var result = await executor.InvestigateAsync(userRequest, purpose, sql, cancellationToken);
        return result is null
            ? McpQueryResponse.Failed(
                executor.LastInvestigationFailure ?? "Investigation validation or execution failed.")
            : BuildResponse(result);
    }

    private ValidatedSqlExecutor CreateExecutor(DatabaseContext context, int? userId)
    {
        var validator = new SqlDeterministicValidator(
            context.PublicTables,
            appConfig.Value.DefaultQueryRowLimit);
        return new ValidatedSqlExecutor(
            new ConsoleOutputService(),
            appConfig.Value,
            context.SecurityFilter,
            tableResultFormatter,
            new DeterministicSqlApprovalSession(validator),
            userId,
            context.ConnectionString,
            DatabaseContextService.CreateDatabase);
    }

    internal static SecurityUserResolution ResolveSecurityUser(
        string? headerValue,
        DatabaseContext context,
        bool allowUnfiltered = false)
    {
        var rawHeader = headerValue?.Trim() ?? string.Empty;
        if (context.RequiresSecurityUser && context.SecurityUsers.Count == 0)
        {
            return SecurityUserResolution.Rejected(
                "The configured security policy requires a user, but list_security_users returned no selectable identities.");
        }

        if (string.IsNullOrEmpty(rawHeader))
        {
            if (allowUnfiltered)
            {
                return SecurityUserResolution.Allowed(null);
            }

            return context.RequiresSecurityUser
                ? SecurityUserResolution.Rejected(
                    $"{UserHeaderName} is required. Call list_security_users, present the returned identities as a menu, and supply the selected ID.")
                : SecurityUserResolution.Allowed(null);
        }

        if (!context.RequiresSecurityUser)
        {
            return SecurityUserResolution.Rejected(
                $"{UserHeaderName} must not be supplied because this database has no selectable security users.");
        }

        if (!int.TryParse(rawHeader, out var userId))
        {
            return SecurityUserResolution.Rejected(
                $"{UserHeaderName} must contain an integer returned by list_security_users.");
        }

        return context.SecurityUsers.ContainsKey(userId)
            ? SecurityUserResolution.Allowed(userId)
            : SecurityUserResolution.Rejected(
                $"{UserHeaderName} does not identify a user returned by list_security_users.");
    }

    private static McpQueryResponse BuildResponse(ValidatedSqlExecutionResult result)
    {
        var table = result.Result;
        var rendered = result.RenderedTable;
        var rows = new List<IReadOnlyDictionary<string, object?>>(rendered.ShownRows);
        for (var rowIndex = 0; rowIndex < rendered.ShownRows; rowIndex++)
        {
            var row = new Dictionary<string, object?>(rendered.ShownColumns, StringComparer.Ordinal);
            for (var columnIndex = 0; columnIndex < rendered.ShownColumns; columnIndex++)
            {
                var value = table.Rows[rowIndex][columnIndex];
                row[table.Columns[columnIndex].ColumnName] = value is DBNull ? null : value;
            }

            rows.Add(row);
        }

        return new McpQueryResponse(
            true,
            null,
            result.ApprovedSql,
            result.ExecutedSql,
            rendered.TotalRows,
            rendered.TotalColumns,
            rendered.ShownRows,
            rendered.ShownColumns,
            rendered.Truncated,
            rows);
    }

    internal sealed record SecurityUserResolution(bool Success, int? UserId, string? Error)
    {
        public static SecurityUserResolution Allowed(int? userId) => new(true, userId, null);
        public static SecurityUserResolution Rejected(string error) => new(false, null, error);
    }
}

public sealed record McpSecurityUser(int Id, string DisplayName);

public sealed record McpQueryResponse(
    bool Success,
    string? Error,
    string? ApprovedSql,
    string? ExecutedSql,
    int TotalRows,
    int TotalColumns,
    int ShownRows,
    int ShownColumns,
    bool Truncated,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    public static McpQueryResponse Failed(string error) =>
        new(false, error, null, null, 0, 0, 0, 0, false, []);
}
