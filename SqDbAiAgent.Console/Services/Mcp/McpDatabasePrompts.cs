using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerPromptType]
public sealed class McpDatabasePrompts(
    McpAgentInstructionsProviderService instructionsProvider,
    ILogger<McpDatabasePrompts> logger)
{
    [McpServerPrompt(Name = McpContractNames.DatabaseAgentPrompt)]
    [Description("Authoritative instructions for using this database agent's schema and safe SQL tools.")]
    public string DatabaseAgent()
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpPromptCall", ("prompt", McpContractNames.DatabaseAgentPrompt));
        var result = instructionsProvider.GetInstructions();
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpPromptResult", ("prompt", McpContractNames.DatabaseAgentPrompt), ("result", result));
        return result;
    }
}
