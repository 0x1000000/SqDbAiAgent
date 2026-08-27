namespace SqDbAiAgent.ConsoleApp.Services.Sql;

public sealed class DeterministicSqlApprovalSession(SqlDeterministicValidator validator) : ISqlApprovalSession
{
    public Task<SqlApprovalResult> ApproveAsync(
        string userRequest,
        string proposedSql,
        string? error = null,
        string errorKind = "parser",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            string.IsNullOrWhiteSpace(error)
                ? validator.Validate(proposedSql)
                : SqlApprovalResult.Failed($"SQL Server rejected the query: {error}"));
    }
}
