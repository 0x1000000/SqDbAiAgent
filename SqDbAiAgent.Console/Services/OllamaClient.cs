using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class OllamaClient(HttpClient httpClient, ILlmInteractionLogger interactionLogger) : ILlmClient
{
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/tags", cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: cancellationToken);
        if (payload?.Models is null)
        {
            return Array.Empty<string>();
        }

        return payload.Models
            .Select(model => model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        CancellationToken cancellationToken = default)
    {
        var request = new OllamaChatRequest
        {
            Model = model,
            Stream = false,
            Format = format,
            Think = ToWireThinkValue(thinkLevel),
            Options = new OllamaChatOptions
            {
                Temperature = 0
            },
            Messages = messages
                .Select(message => new OllamaChatMessageDto
                {
                    Role = message.Role,
                    Content = message.Content
                })
                .ToList()
        };
        if (interactionLogger.IsEnabled)
        {
            var requestJson = JsonSerializer.Serialize(request, JsonSerializerOptions);
            await interactionLogger.LogAsync(
                $$"""
                ===== OLLAMA REQUEST {{DateTime.UtcNow:O}} =====
                POST /api/chat
                {{requestJson}}
                """,
                cancellationToken);
        }

        using var response = await httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (interactionLogger.IsEnabled)
        {
            await interactionLogger.LogAsync(
                $$"""
                ===== OLLAMA RESPONSE {{DateTime.UtcNow:O}} =====
                {{responseJson}}
                """,
                cancellationToken);
        }

        var payload = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, JsonSerializerOptions);
        var content = payload?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama returned an empty chat response.");
        }

        return content;
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelDto>? Models { get; init; }
    }

    private sealed class OllamaModelDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("format")]
        public JsonElement? Format { get; init; }

        [JsonPropertyName("think")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Think { get; init; }

        [JsonPropertyName("options")]
        public OllamaChatOptions? Options { get; init; }

        [JsonPropertyName("messages")]
        public List<OllamaChatMessageDto> Messages { get; init; } = [];
    }

    private sealed class OllamaChatOptions
    {
        [JsonPropertyName("temperature")]
        public int Temperature { get; init; }
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessageDto? Message { get; init; }
    }

    private sealed class OllamaChatMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    private static object? ToWireThinkValue(LlmThinkLevel thinkLevel)
    {
        return thinkLevel switch
        {
            LlmThinkLevel.Default => null,
            LlmThinkLevel.Disabled => false,
            LlmThinkLevel.Enabled => true,
            LlmThinkLevel.Low => "low",
            LlmThinkLevel.Medium => "medium",
            LlmThinkLevel.High => "high",
            _ => null
        };
    }
}
