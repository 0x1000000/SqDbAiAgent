using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class OpenRouterClient(
    HttpClient httpClient,
    ILlmInteractionLogger interactionLogger,
    IOptions<OpenRouterOptions> options) : ILlmClient
{
    private readonly OpenRouterOptions _options = options.Value;

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        ApplyHeaders(request.Headers);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OpenRouterModelsResponse>(
            JsonSerializerOptions,
            cancellationToken);
        if (payload?.Data is null)
        {
            return Array.Empty<string>();
        }

        return payload.Data
            .Select(model => model.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        CancellationToken cancellationToken = default)
    {
        var request = new OpenRouterChatRequest
        {
            Model = model,
            Stream = false,
            Temperature = 0,
            Messages = messages
                .Select(message => new OpenRouterChatMessageDto
                {
                    Role = message.Role,
                    Content = message.Content
                })
                .ToList(),
            ResponseFormat = ToResponseFormat(format),
            Reasoning = ToReasoning(thinkLevel)
        };

        if (interactionLogger.IsEnabled)
        {
            var requestJson = JsonSerializer.Serialize(request, JsonSerializerOptions);
            await interactionLogger.LogAsync(
                $$"""
                ===== OPENROUTER REQUEST {{DateTime.UtcNow:O}} =====
                POST chat/completions
                {{requestJson}}
                """,
                cancellationToken);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(request, options: JsonSerializerOptions)
        };
        ApplyHeaders(httpRequest.Headers);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (interactionLogger.IsEnabled)
        {
            await interactionLogger.LogAsync(
                $$"""
                ===== OPENROUTER RESPONSE {{DateTime.UtcNow:O}} =====
                {{responseJson}}
                """,
                cancellationToken);
        }

        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<OpenRouterChatResponse>(responseJson, JsonSerializerOptions);
        var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenRouter returned an empty chat response.");
        }

        return content;
    }

    private void ApplyHeaders(HttpRequestHeaders headers)
    {
        headers.Authorization = new AuthenticationHeaderValue("Bearer", this._options.ApiKey);

        if (!string.IsNullOrWhiteSpace(this._options.Referer))
        {
            headers.TryAddWithoutValidation("HTTP-Referer", this._options.Referer);
        }

        if (!string.IsNullOrWhiteSpace(this._options.Title))
        {
            headers.TryAddWithoutValidation("X-OpenRouter-Title", this._options.Title);
        }
    }

    private static OpenRouterResponseFormat? ToResponseFormat(JsonElement? format)
    {
        if (format is not { ValueKind: JsonValueKind.Object } schema)
        {
            return null;
        }

        return new OpenRouterResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new OpenRouterJsonSchema
            {
                Name = "response",
                Strict = true,
                Schema = schema
            }
        };
    }

    private static OpenRouterReasoning? ToReasoning(LlmThinkLevel thinkLevel)
    {
        return thinkLevel switch
        {
            LlmThinkLevel.Default => null,
            LlmThinkLevel.Disabled => new OpenRouterReasoning
            {
                Effort = "none",
                Exclude = true
            },
            LlmThinkLevel.Enabled => new OpenRouterReasoning
            {
                Enabled = true,
                Exclude = true
            },
            LlmThinkLevel.Low => new OpenRouterReasoning
            {
                Effort = "low",
                Exclude = true
            },
            LlmThinkLevel.Medium => new OpenRouterReasoning
            {
                Effort = "medium",
                Exclude = true
            },
            LlmThinkLevel.High => new OpenRouterReasoning
            {
                Effort = "high",
                Exclude = true
            },
            _ => null
        };
    }

    private sealed class OpenRouterModelsResponse
    {
        [JsonPropertyName("data")]
        public List<OpenRouterModelDto>? Data { get; init; }
    }

    private sealed class OpenRouterModelDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
    }

    private sealed class OpenRouterChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OpenRouterChatMessageDto> Messages { get; init; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("temperature")]
        public int Temperature { get; init; }

        [JsonPropertyName("response_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenRouterResponseFormat? ResponseFormat { get; init; }

        [JsonPropertyName("reasoning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OpenRouterReasoning? Reasoning { get; init; }
    }

    private sealed class OpenRouterChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenRouterChoiceDto>? Choices { get; init; }
    }

    private sealed class OpenRouterChoiceDto
    {
        [JsonPropertyName("message")]
        public OpenRouterChatMessageDto? Message { get; init; }
    }

    private sealed class OpenRouterChatMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }

    private sealed class OpenRouterResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("json_schema")]
        public OpenRouterJsonSchema? JsonSchema { get; init; }
    }

    private sealed class OpenRouterJsonSchema
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("strict")]
        public bool Strict { get; init; }

        [JsonPropertyName("schema")]
        public JsonElement Schema { get; init; }
    }

    private sealed class OpenRouterReasoning
    {
        [JsonPropertyName("enabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Enabled { get; init; }

        [JsonPropertyName("effort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Effort { get; init; }

        [JsonPropertyName("exclude")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Exclude { get; init; }
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };
}
