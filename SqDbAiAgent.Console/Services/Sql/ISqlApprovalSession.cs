namespace SqDbAiAgent.ConsoleApp.Services.Sql;

public interface ISqlApprovalSession
{
    Task<SqlApprovalResult> ApproveAsync(
        string userRequest,
        string proposedSql,
        string? error = null,
        string errorKind = "parser",
        CancellationToken cancellationToken = default);
}
