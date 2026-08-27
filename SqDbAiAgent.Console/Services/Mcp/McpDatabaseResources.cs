using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

[McpServerResourceType]
public sealed class McpDatabaseResources(DatabaseContextService databaseContextService)
{
    [McpServerResource(UriTemplate = McpContractNames.DatabaseSchemaResource, Name = McpContractNames.DatabaseSchemaResourceName, MimeType = "application/json")]
    [Description("Complete public database schema and relationships. No database security identity is required to read this resource.")]
    public async Task<string> DatabaseSchema(CancellationToken cancellationToken = default) =>
        (await databaseContextService.GetAsync(cancellationToken)).SchemaPrompt;
}
