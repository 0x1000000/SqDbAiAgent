using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class ValidatedSqlExecutor(
    IConsoleOutput output,
    AppConfig appConfig,
    ISecurityFilter securityFilter,
    ITablePrinter tablePrinter,
    IAgentTableFormatter agentTableFormatter,
    ISqlApprovalSession sqlApprovalSession,
    int? userId,
    string connectionString,
    Func<string, ISqDatabase> dbFactory)
{
    public async Task<ValidatedSqlExecutionResult?> SubmitAsync(
        string userRequest,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var approval = await sqlApprovalSession.ApproveAsync(userRequest, sql, cancellationToken: cancellationToken);
        if (!approval.Success)
        {
            output.OutErrorLine(approval.FailureMessage);
            return null;
        }

        var result = await this.ExecuteApprovedAsync(userRequest, approval, cancellationToken);
        if (result is not null)
        {
            tablePrinter.Print(result.Result);
        }

        return result;
    }

    public async Task<ValidatedSqlExecutionResult?> ExecuteApprovedAsync(
        string userRequest,
        SqlApprovalResult approvalResult,
        CancellationToken cancellationToken = default)
    {
        var currentApproval = approvalResult;
        for (var attempt = 1; attempt <= appConfig.MaxSqlRuntimeFixAttempts + 1; attempt++)
        {
            if (!TryGetExecutableReadOnlyQuery(currentApproval, out var query))
            {
                return null;
            }

            if (!securityFilter.ValidateQuery(query, userId, out var safeQuery, out var error))
            {
                output.OutErrorLine(error);
                return null;
            }

            query = (IExprQuery)safeQuery;
            WriteAcceptedQuery(currentApproval.ApprovedSql, query);

            try
            {
                var result = await this.ExecuteQueryAsync(query, cancellationToken);
                return new ValidatedSqlExecutionResult(
                    currentApproval.ApprovedSql,
                    result,
                    agentTableFormatter.RenderMarkdown(result, appConfig.MaxAgentVisibleCells));
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
                    output.OutErrorLine("Could not execute query :( " + sqlException.Message);
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
                    output.OutErrorLine(currentApproval.FailureMessage);
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

    private async Task<DataTable> ExecuteQueryAsync(IExprQuery expression, CancellationToken cancellationToken)
    {
        await using var database = dbFactory(connectionString);
        return await database.Query(
            expression,
            new DataTable(),
            (table, reader) =>
            {
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
}

public sealed record ValidatedSqlExecutionResult(string ApprovedSql, DataTable Result, RenderedTable RenderedTable);
