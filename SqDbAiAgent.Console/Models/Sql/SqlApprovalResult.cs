using SqExpress.Syntax;

namespace SqDbAiAgent.ConsoleApp.Models.Sql;

public sealed class SqlApprovalResult
{
    public bool Success { get; init; }

    public string ApprovedSql { get; init; } = string.Empty;

    public IExpr? ParsedExpression { get; init; }

    public string FailureMessage { get; init; } = string.Empty;

    public bool DefaultRowLimitApplied { get; init; }

    public static SqlApprovalResult Approved(
        string sql,
        IExpr parsedExpression,
        bool defaultRowLimitApplied = false)
    {
        return new SqlApprovalResult
        {
            Success = true,
            ApprovedSql = sql,
            ParsedExpression = parsedExpression,
            DefaultRowLimitApplied = defaultRowLimitApplied
        };
    }

    public static SqlApprovalResult Failed(string failureMessage)
    {
        return new SqlApprovalResult
        {
            Success = false,
            FailureMessage = failureMessage
        };
    }
}
