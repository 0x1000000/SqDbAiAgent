using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerResourceType]
public sealed class McpDatabaseResources(
    DatabaseContextService databaseContextService,
    ILogger<McpDatabaseResources> logger)
{
    [McpServerResource(UriTemplate = McpContractNames.DatabaseSchemaResource, Name = McpContractNames.DatabaseSchemaResourceName, MimeType = "application/json")]
    [Description("Complete public database schema and relationships. No database security identity is required to read this resource.")]
    public async Task<string> DatabaseSchema(CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpResourceCall", ("resource", McpContractNames.DatabaseSchemaResource));
        var result = (await databaseContextService.GetAsync(cancellationToken)).SchemaPrompt;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformationEvent("McpResourceCompleted", ("resource", McpContractNames.DatabaseSchemaResource),
                ("success", true), ("durationMs", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebugEvent("McpResourceResult", ("resource", McpContractNames.DatabaseSchemaResource), ("result", result));
        return result;
    }
}
