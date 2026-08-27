using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerPromptType]
public sealed class McpDatabasePrompts(McpAgentInstructionsProviderService instructionsProvider)
{
    [McpServerPrompt(Name = McpContractNames.DatabaseAgentPrompt)]
    [Description("Authoritative instructions for using this database agent's schema and safe SQL tools.")]
    public string DatabaseAgent() => instructionsProvider.GetInstructions();
}
