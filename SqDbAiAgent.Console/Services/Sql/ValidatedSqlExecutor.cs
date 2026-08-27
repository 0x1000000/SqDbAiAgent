using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services.Sql;

public sealed class ValidatedSqlExecutor(
    IConsoleOutput output,
    AppConfig appConfig,
    ISecurityFilter securityFilter,
    TableResultFormatterService tableResultFormatter,
    ISqlApprovalSession sqlApprovalSession,
    int? userId,
    string connectionString,
    Func<string, ISqDatabase> dbFactory)
{
    public string? LastInvestigationFailure { get; private set; }
    public string? LastFailure { get; private set; }

    public async Task<ValidatedSqlExecutionResult?> SubmitAsync(
        string userRequest,
        string sql,
        CancellationToken cancellationToken = default)
    {
        return await this.SubmitCoreAsync(
            userRequest,
            sql,
            appConfig.MaxAgentVisibleCells,
            printResult: true,
            investigationPurpose: null,
            materializedRowLimit: null,
            cancellationToken);
    }

    public async Task<ValidatedSqlExecutionResult?> SubmitForAgentAsync(
        string userRequest,
        string sql,
        CancellationToken cancellationToken = default)
    {
        this.LastFailure = null;
        return await this.SubmitCoreAsync(
            userRequest,
            sql,
            appConfig.MaxAgentVisibleCells,
            printResult: false,
            investigationPurpose: null,
            materializedRowLimit: appConfig.MaxAgentVisibleCells,
            cancellationToken);
    }

    public async Task<ValidatedSqlExecutionResult?> InvestigateAsync(
        string userRequest,
        string purpose,
        string sql,
        CancellationToken cancellationToken = default)
    {
        this.LastInvestigationFailure = null;
        if (string.IsNullOrWhiteSpace(purpose))
        {
            output.OutDebugLine("Investigation rejected: purpose must be a non-empty string.");
            output.OutDebugLine(string.Empty);
            return null;
        }

        if (Regex.IsMatch(
                purpose,
                @"\b(?:structure|schema|relationships?|sample\s+(?:records?|data))\b",
                RegexOptions.IgnoreCase))
        {
            const string error =
                "Investigation purpose must resolve one concrete data uncertainty, not explore schema or sample data.";
            this.WriteFailure(error, purpose);
            return null;
        }

        output.OutDebugLine($"Investigation purpose: {purpose.Trim()}");
        return await this.SubmitCoreAsync(
            userRequest,
            sql,
            appConfig.MaxInvestigationVisibleCells,
            printResult: false,
            investigationPurpose: purpose.Trim(),
            materializedRowLimit: appConfig.MaxInvestigationVisibleCells,
            cancellationToken);
    }

    private async Task<ValidatedSqlExecutionResult?> SubmitCoreAsync(
        string userRequest,
        string sql,
        int visibleCellLimit,
        bool printResult,
        string? investigationPurpose,
        int? materializedRowLimit,
        CancellationToken cancellationToken)
    {
        var approval = await sqlApprovalSession.ApproveAsync(userRequest, sql, cancellationToken: cancellationToken);
        if (!approval.Success)
        {
            this.WriteFailure(approval.FailureMessage, investigationPurpose);
            return null;
        }

        var result = await this.ExecuteApprovedAsync(
            userRequest,
            approval,
            visibleCellLimit,
            investigationPurpose,
            materializedRowLimit,
            cancellationToken);
        if (result is not null && printResult)
        {
            tableResultFormatter.Print(result.Result);
        }
        else if (result is not null && investigationPurpose is not null)
        {
            output.OutDebugLine(
                $"Investigation result: {result.RenderedTable.TotalRows} row(s); "
                + $"{result.RenderedTable.ShownCells} visible cell(s); "
                + $"truncated={result.RenderedTable.Truncated.ToString().ToLowerInvariant()}.");
            output.OutDebugLine(string.Empty);
        }

        return result;
    }

    private async Task<ValidatedSqlExecutionResult?> ExecuteApprovedAsync(
        string userRequest,
        SqlApprovalResult approvalResult,
        int visibleCellLimit,
        string? investigationPurpose,
        int? materializedRowLimit,
        CancellationToken cancellationToken)
    {
        var currentApproval = approvalResult;
        for (var attempt = 1; attempt <= appConfig.MaxSqlRuntimeFixAttempts + 1; attempt++)
        {
            if (investigationPurpose is not null
                && currentApproval.DefaultRowLimitApplied
                && currentApproval.ParsedExpression is IExprReadOnlyQuery limitedQuery)
            {
                var investigationLimit = Math.Min(
                    appConfig.DefaultQueryRowLimit,
                    appConfig.MaxInvestigationVisibleCells);
                var adjustedQuery = SqlQueryLimiter.ReplaceAppliedDefault(
                    limitedQuery,
                    investigationLimit);
                currentApproval = SqlApprovalResult.Approved(
                    adjustedQuery.ToSql(TSqlExporter.Default),
                    adjustedQuery,
                    true);
            }

            if (investigationPurpose is not null
                && !ValidateInvestigationSql(currentApproval.ApprovedSql, visibleCellLimit, out var investigationError))
            {
                this.WriteFailure(investigationError, investigationPurpose);
                return null;
            }

            if (!TryGetExecutableReadOnlyQuery(currentApproval, out var query))
            {
                this.WriteFailure("Only read-only queries are supported.", investigationPurpose);
                return null;
            }

            if (!securityFilter.ValidateQuery(query, userId, out var safeQuery, out var error))
            {
                this.WriteFailure(error, investigationPurpose);
                return null;
            }

            query = (IExprQuery)safeQuery;
            WriteAcceptedQuery(currentApproval.ApprovedSql, query);

            try
            {
                var result = await this.ExecuteQueryAsync(
                    query,
                    materializedRowLimit,
                    cancellationToken);
                return new ValidatedSqlExecutionResult(
                    currentApproval.ApprovedSql,
                    result,
                    tableResultFormatter.RenderMarkdown(result, visibleCellLimit),
                    query.ToSql(TSqlExporter.Default));
            }
            catch (Exception ex)
            {
                var sqlException = FindSqlException(ex);
                if (sqlException is null)
                {
                    throw;
                }

                output.OutDebugLine($"SQL runtime failed on attempt {attempt}: {sqlException.Message}");
                output.OutDebugLine("Wrong T-SQL:");
                output.OutDebugLine(currentApproval.ApprovedSql);
                output.OutDebugLine(string.Empty);

                if (attempt > appConfig.MaxSqlRuntimeFixAttempts)
                {
                    this.WriteFailure("Could not execute query: " + sqlException.Message, investigationPurpose);
                    return null;
                }

                currentApproval = await sqlApprovalSession.ApproveAsync(
                    userRequest,
                    currentApproval.ApprovedSql,
                    sqlException.Message,
                    "runtime",
                    cancellationToken);
                if (!currentApproval.Success)
                {
                    this.WriteFailure(currentApproval.FailureMessage, investigationPurpose);
                    return null;
                }
            }
        }

        return null;
    }

    private static bool TryGetExecutableReadOnlyQuery(
        SqlApprovalResult approvalResult,
        [NotNullWhen(true)] out IExprQuery? query)
    {
        query = approvalResult.ParsedExpression as IExprQuery;
        return query is IExprReadOnlyQuery;
    }

    private async Task<DataTable> ExecuteQueryAsync(
        IExprQuery expression,
        int? maximumRows,
        CancellationToken cancellationToken)
    {
        await using var database = dbFactory(connectionString);
        return await database.Query(
            expression,
            new DataTable(),
            (table, reader) =>
            {
                // Retain one sentinel row so rendering reports truncation at the hard MCP limit.
                if (maximumRows.HasValue && table.Rows.Count > maximumRows.Value)
                {
                    return table;
                }

                if (table.Columns.Count == 0)
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        table.Columns.Add(GetUniqueColumnName(table, reader.GetName(i)));
                    }
                }

                var row = table.NewRow();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.GetValue(i);
                }

                table.Rows.Add(row);
                return table;
            },
            cancellationToken);
    }

    private static string GetUniqueColumnName(DataTable table, string columnName)
    {
        var baseName = string.IsNullOrWhiteSpace(columnName) ? "Column" : columnName;
        if (!table.Columns.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (table.Columns.Contains($"{baseName}_{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}_{suffix}";
    }

    private static SqlException? FindSqlException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
    }

    private void WriteAcceptedQuery(string sql, IExprQuery query)
    {
        output.OutDebugLine("Accepted query: ");
        output.OutDebug(sql);
        output.OutDebugLine(string.Empty);
        output.OutDebugLine("Query to execute: ");
        output.OutDebugLine(query.ToSql(TSqlExporter.Default));
        output.OutDebugLine(string.Empty);
    }

    private void WriteFailure(string message, string? investigationPurpose)
    {
        if (investigationPurpose is null)
        {
            this.LastFailure = message;
            output.OutErrorLine(message);
            return;
        }

        output.OutDebugLine($"Investigation failed: {message}");
        output.OutDebugLine(string.Empty);
        this.LastInvestigationFailure = message;
    }

    internal static bool ValidateInvestigationSql(string sql, int maximumRows, out string error)
    {
        if (Regex.IsMatch(sql, @"\b(?:UNION|INTERSECT|EXCEPT)\b", RegexOptions.IgnoreCase))
        {
            error = "Investigation queries cannot combine result sets.";
            return false;
        }

        if (Regex.IsMatch(
                sql,
                @"\bSELECT\s+(?:DISTINCT\s+)?(?:TOP\s*\(?\s*\d+\s*\)?\s+)?(?:\w+\.)?\*",
                RegexOptions.IgnoreCase))
        {
            error = "Investigation queries cannot use SELECT *.";
            return false;
        }

        if (Regex.IsMatch(
                sql,
                @"^\s*SELECT\s+(?:DISTINCT\s+)?(?:TOP\s*\(?\s*\d+\s*\)?\s+)?(?:COUNT|MIN|MAX|AVG|SUM)\s*\(",
                RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(sql, @"\bGROUP\s+BY\b", RegexOptions.IgnoreCase))
            {
                error = "Aggregate investigation queries must return one row and cannot use GROUP BY.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        var topMatch = Regex.Match(
            sql,
            @"^\s*SELECT\s+(?:DISTINCT\s+)?TOP\s*\(?\s*(\d+)\s*\)?",
            RegexOptions.IgnoreCase);
        if (Regex.IsMatch(sql, @"\bTOP\s*\(?\s*\d+\s*\)?\s+(?:PERCENT|WITH\s+TIES)\b", RegexOptions.IgnoreCase))
        {
            error = "Investigation TOP cannot use PERCENT or WITH TIES.";
            return false;
        }

        if (!topMatch.Success
            || !int.TryParse(topMatch.Groups[1].Value, out var top)
            || top < 1
            || top > maximumRows)
        {
            error = $"Investigation queries must use TOP between 1 and {maximumRows}, or return a single aggregate value.";
            return false;
        }

        if (!Regex.IsMatch(sql, @"^\s*SELECT\s+DISTINCT\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(sql, @"\bWHERE\b", RegexOptions.IgnoreCase))
        {
            error = "Row-returning investigation queries must use a selective WHERE clause or SELECT DISTINCT.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public sealed record ValidatedSqlExecutionResult(
    string ApprovedSql,
    DataTable Result,
    RenderedTable RenderedTable,
    string ExecutedSql);
