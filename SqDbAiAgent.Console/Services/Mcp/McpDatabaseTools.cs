using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerToolType]
public sealed class McpDatabaseTools(
    McpDatabaseService databaseService,
    DatabaseContextService databaseContextService,
    ILogger<McpDatabaseTools> logger)
{
    [McpServerTool(Name = McpContractNames.GetDatabaseSchemaTool)]
    [Description("Returns the complete public database schema, including every available table, column, and inferred relationship. No security identity is required.")]
    public async Task<string> GetDatabaseSchema(CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpToolCall", ("tool", McpContractNames.GetDatabaseSchemaTool));
        var result = (await databaseContextService.GetAsync(cancellationToken)).SchemaPrompt;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformationEvent("McpToolCompleted", ("tool", McpContractNames.GetDatabaseSchemaTool),
                ("success", true), ("durationMs", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpToolResult", ("tool", McpContractNames.GetDatabaseSchemaTool), ("result", result));
        return result;
    }

    [McpServerTool(Name = McpContractNames.SubmitSqlTool)]
    [Description("Validates and executes one final self-contained read-only Microsoft SQL Server query using the host's security context. No LLM repair is performed.")]
    public Task<McpQueryResponse> SubmitSql(
        [Description("The original user request this SQL is intended to answer.")] string userRequest,
        [Description("One self-contained read-only Microsoft SQL Server query.")] string sql,
        CancellationToken cancellationToken = default) =>
        databaseService.SubmitAsync(userRequest, sql, cancellationToken);

    [McpServerTool(Name = McpContractNames.InvestigateSqlTool)]
    [Description("Runs one narrow internal read-only evidence query using the host's security context.")]
    public Task<McpQueryResponse> InvestigateSql(
        [Description("The original user request that created the uncertainty.")] string userRequest,
        [Description("The exact literal or filter uncertainty this probe resolves.")] string purpose,
        [Description("A narrow read-only T-SQL query using TOP with a selective WHERE clause, or a single aggregate.")] string sql,
        CancellationToken cancellationToken = default) =>
        databaseService.InvestigateAsync(
            userRequest,
            purpose,
            sql,
            cancellationToken);
}
