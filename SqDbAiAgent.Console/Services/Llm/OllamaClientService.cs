using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Llm;

public sealed class OllamaClientService(HttpClient httpClient, ILogger<OllamaClientService> logger) : ILlmClient
{
    public async Task<LlmModelCapabilities> GetModelCapabilitiesAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/show", new { model }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<OllamaShowResponse>(cancellationToken: cancellationToken);
        return new LlmModelCapabilities(
            payload?.Capabilities?.Contains("tools", StringComparer.OrdinalIgnoreCase) == true);
    }

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

    public async Task<LlmChatResult> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        IReadOnlyList<LlmToolDefinition>? tools = null,
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
                    Content = message.Content,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.Name,
                    ToolCalls = message.ToolCalls?.Select(ToToolCallDto).ToList()
                })
                .ToList(),
            Tools = tools?.Select(ToToolDto).ToList()
        };
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugEvent("LlmRequest",
                ("provider", "Ollama"), ("model", model), ("endpoint", "/api/chat"),
                ("request", request));
        }

        using var response = await httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugEvent("LlmResponse",
                ("provider", "Ollama"), ("model", model), ("response", ParseJson(responseJson)));
        }

        var payload = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, JsonSerializerOptions);
        var content = payload?.Message?.Content ?? string.Empty;
        var toolCalls = payload?.Message?.ToolCalls?
            .Select((call, index) => new LlmToolCall(
                string.IsNullOrWhiteSpace(call.Id) ? $"call_{index + 1}_{Guid.NewGuid():N}" : call.Id,
                call.Function?.Name ?? string.Empty,
                call.Function?.Arguments ?? EmptyObject))
            .ToArray() ?? [];

        if (string.IsNullOrWhiteSpace(content) && toolCalls.Length == 0)
        {
            throw new InvalidOperationException("Ollama returned an empty chat response.");
        }

        return new LlmChatResult(content, toolCalls);
    }

    private static OllamaToolDto ToToolDto(LlmToolDefinition tool) => new()
    {
        Type = "function",
        Function = new OllamaFunctionDto
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = tool.Parameters
        }
    };

    private static OllamaToolCallDto ToToolCallDto(LlmToolCall call) => new()
    {
        Id = call.Id,
        Function = new OllamaFunctionCallDto { Name = call.Name, Arguments = call.Arguments }
    };

    private sealed class OllamaShowResponse
    {
        [JsonPropertyName("capabilities")]
        public List<string>? Capabilities { get; init; }
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

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OllamaToolDto>? Tools { get; init; }
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

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OllamaToolCallDto>? ToolCalls { get; init; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolName { get; init; }
    }

    private sealed class OllamaToolDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public OllamaFunctionDto Function { get; init; } = new();
    }

    private sealed class OllamaFunctionDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; init; }
    }

    private sealed class OllamaToolCallDto
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonPropertyName("function")]
        public OllamaFunctionCallDto? Function { get; init; }
    }

    private sealed class OllamaFunctionCallDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; init; }
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

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
