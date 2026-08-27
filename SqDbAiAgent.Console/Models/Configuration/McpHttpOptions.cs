using System.ComponentModel.DataAnnotations;

namespace SqDbAiAgent.ConsoleApp.Models.Configuration;

public sealed class McpHttpOptions
{
    public const string SectionName = "McpHttp";
    public const string ApiKeyPlaceholder = "CHANGE_ME";

    [Required]
    public string Url { get; init; } = "http://localhost:5080";

    public string ApiKey { get; init; } = ApiKeyPlaceholder;

    public bool ConsoleOutputEnabled { get; init; }
}
