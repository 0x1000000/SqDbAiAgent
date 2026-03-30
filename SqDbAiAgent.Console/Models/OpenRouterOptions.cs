using System.ComponentModel.DataAnnotations;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    [Required]
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string Model { get; init; } = "openai/gpt-4o-mini";

    public string? Referer { get; init; }

    public string? Title { get; init; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; init; } = 180;
}
