using SqExpress.Syntax;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed class SqlApprovalResult
{
    public bool Success { get; init; }

    public string ApprovedSql { get; init; } = string.Empty;

    public IExpr? ParsedExpression { get; init; }

    public string FailureMessage { get; init; } = string.Empty;

    public static SqlApprovalResult Approved(string sql, IExpr parsedExpression)
    {
        return new SqlApprovalResult
        {
            Success = true,
            ApprovedSql = sql,
            ParsedExpression = parsedExpression
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
