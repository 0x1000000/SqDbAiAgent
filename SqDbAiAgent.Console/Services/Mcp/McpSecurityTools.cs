using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerToolType]
public sealed class McpSecurityTools(McpDatabaseService databaseService)
{
    [McpServerTool(Name = McpContractNames.ListSecurityUsersTool)]
    [Description("Lists selectable database security identities. The MCP host determines whether and how an identity is selected for data tools.")]
    public Task<IReadOnlyList<McpSecurityUser>> ListSecurityUsers(
        CancellationToken cancellationToken = default) =>
        databaseService.ListSecurityUsersAsync(cancellationToken);
}
