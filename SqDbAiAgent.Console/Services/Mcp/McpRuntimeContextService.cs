using Microsoft.AspNetCore.Http;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

public sealed class McpRuntimeContextService(
    McpTransport transport,
    int databaseUserId,
    string databaseName,
    bool hasSecurityProfile,
    IHttpContextAccessor httpContextAccessor)
{
    public McpTransport Transport { get; } = transport;

    public int DatabaseUserId { get; } = databaseUserId;

    public string DatabaseName { get; } = databaseName;

    public bool HasSecurityProfile { get; } = hasSecurityProfile;

    public string? GetSecurityUserValue() =>
        this.Transport == McpTransport.Http
            ? httpContextAccessor.HttpContext?.Request.Headers[McpContractNames.DatabaseUserHeader].ToString()
            : this.DatabaseUserId == 0 ? null : this.DatabaseUserId.ToString();

    public bool AllowsUnfilteredSecurityContext => this.Transport == McpTransport.Stdio;
}
