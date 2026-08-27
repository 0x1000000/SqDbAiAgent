using System.Text.RegularExpressions;
using SqDbAiAgent.ConsoleApp.Helpers;
using SqExpress;
using SqExpress.SqlParser;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services.Sql;

public sealed class SqlDeterministicValidator(
    IReadOnlyList<TableBase> publicTables,
    int defaultQueryRowLimit = 100)
{
    public SqlApprovalResult Validate(string proposedSql)
    {
        var sql = NormalizeSqlText(proposedSql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlApprovalResult.Failed("SQL must be a non-empty string.");
        }

        if (Regex.IsMatch(
                sql,
                @"\b(?:INFORMATION_SCHEMA|sys\s*\.|OBJECT_ID|COL_NAME|COLUMNPROPERTY)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return SqlApprovalResult.Failed(
                "Database metadata queries are forbidden. Use only the schema exposed by the database://schema MCP resource.");
        }

        if (Regex.IsMatch(
                sql,
                @"(?<!@)@[A-Za-z_][A-Za-z0-9_]*",
                RegexOptions.CultureInvariant))
        {
            return SqlApprovalResult.Failed(
                "Pure SQL parameters are not supported. Submit a self-contained query without placeholders such as @id.");
        }

        if (!SqTSqlParser.TryParse(sql, out var expression, out var parsedTables, out var parseError))
        {
            return SqlApprovalResult.Failed(
                string.IsNullOrWhiteSpace(parseError) ? "Unknown SQL parse error." : parseError);
        }

        var comparison = parsedTables.CompareWith(publicTables, SqExpressHelpers.BuildTableComparisonKey);
        if (comparison is not null)
        {
            var mismatch = BuildParsedTableMismatchError(comparison);
            if (!string.IsNullOrWhiteSpace(mismatch))
            {
                return SqlApprovalResult.Failed(mismatch);
            }
        }

        if (expression is not IExprReadOnlyQuery readOnlyQuery)
        {
            return SqlApprovalResult.Failed("Only read-only SELECT queries are supported.");
        }

        SqlQueryLimitResult limited;
        try
        {
            limited = SqlQueryLimiter.ApplyDefault(readOnlyQuery, defaultQueryRowLimit);
        }
        catch (NotSupportedException ex)
        {
            return SqlApprovalResult.Failed(ex.Message);
        }

        var approvedSql = limited.Applied
            ? limited.Query.ToSql(TSqlExporter.Default)
            : sql;
        return SqlApprovalResult.Approved(approvedSql, limited.Query, limited.Applied);
    }

    private string? BuildParsedTableMismatchError(TableListComparison comparison)
    {
        var unexpectedTables = comparison.MissedTables
            .Select(SqExpressHelpers.FormatTableName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var differences = new List<string>();
        var allowedColumns = new List<string>();

        foreach (var difference in comparison.DifferentTables.OrderBy(
                     item => SqExpressHelpers.BuildTableComparisonKey(item.Table.FullName),
                     StringComparer.Ordinal))
        {
            var message = SqExpressHelpers.BuildTableDifferenceMessage(
                difference.Table,
                difference.TableComparison);
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            differences.Add(message);
            var matchingTable = SqExpressHelpers.FindMatchingTable(publicTables, difference.Table)
                                ?? difference.Table;
            allowedColumns.Add(
                $"Allowed columns for {SqExpressHelpers.FormatTableName(matchingTable)}: "
                + string.Join(", ", SqExpressHelpers.GetAvailableColumns(matchingTable).Select(column => $"[{column}]")));
        }

        if (unexpectedTables.Length == 0 && differences.Count == 0)
        {
            return null;
        }

        var parts = new List<string> { "SQL references tables or columns outside the exposed database schema." };
        if (unexpectedTables.Length > 0)
        {
            parts.Add("Unexpected tables: " + string.Join(", ", unexpectedTables));
        }

        if (differences.Count > 0)
        {
            parts.Add("Table differences: " + string.Join("; ", differences));
        }

        parts.AddRange(allowedColumns.Distinct(StringComparer.Ordinal));
        return string.Join(Environment.NewLine, parts);
    }

    private static string NormalizeSqlText(string sql)
    {
        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine >= 0)
        {
            trimmed = trimmed[(firstNewLine + 1)..];
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence >= 0 ? trimmed[..closingFence] : trimmed).Trim();
    }
}
