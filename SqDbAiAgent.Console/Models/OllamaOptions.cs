using System.ComponentModel.DataAnnotations;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    [Required]
    public string BaseUrl { get; init; } = "http://localhost:11434";

    [Required]
    public string Model { get; init; } = "qwen2.5-coder:3b";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; init; } = 180;
}
