using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Llm;

public sealed class OpenRouterClientService(
    HttpClient httpClient,
    ILogger<OpenRouterClientService> logger,
    IOptions<OpenRouterOptions> options) : ILlmClient
{
    private readonly OpenRouterOptions _options = options.Value;

    public async Task<LlmModelCapabilities> GetModelCapabilitiesAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        var models = await this.GetModelsAsync(cancellationToken);
        var selected = models.FirstOrDefault(item => string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase));
        return new LlmModelCapabilities(
            selected?.SupportedParameters?.Contains("tools", StringComparer.OrdinalIgnoreCase) == true);
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await this.GetModelsAsync(cancellationToken);

        return models
            .Select(model => model.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<OpenRouterModelDto>> GetModelsAsync(CancellationToken cancellationToken)
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
            return [];
        }

        return payload.Data;
    }

    public async Task<LlmChatResult> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        IReadOnlyList<LlmToolDefinition>? tools = null,
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
                    Content = message.Content,
                    ToolCallId = message.ToolCallId,
                    Name = message.Name,
                    ToolCalls = message.ToolCalls?.Select(ToToolCallDto).ToList()
                })
                .ToList(),
            ResponseFormat = ToResponseFormat(format),
            Reasoning = ToReasoning(thinkLevel),
            Tools = tools?.Select(ToToolDto).ToList(),
            ToolChoice = tools is { Count: > 0 } ? "auto" : null
        };

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugEvent("LlmRequest",
                ("provider", "OpenRouter"), ("model", model), ("endpoint", "chat/completions"),
                ("request", request));
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(request, options: JsonSerializerOptions)
        };
        ApplyHeaders(httpRequest.Headers);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugEvent("LlmResponse",
                ("provider", "OpenRouter"), ("model", model), ("response", ParseJsonOrRaw(responseJson)));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenRouter request failed with status {(int)response.StatusCode} ({response.StatusCode}): {GetErrorMessage(responseJson)}",
                inner: null,
                response.StatusCode);
        }

        var payload = JsonSerializer.Deserialize<OpenRouterChatResponse>(responseJson, JsonSerializerOptions);
        var message = payload?.Choices?.FirstOrDefault()?.Message;
        var content = message?.Content ?? string.Empty;
        var toolCalls = message?.ToolCalls?
            .Select((call, index) => new LlmToolCall(
                string.IsNullOrWhiteSpace(call.Id) ? $"call_{index + 1}_{Guid.NewGuid():N}" : call.Id,
                call.Function?.Name ?? string.Empty,
                ParseArguments(call.Function?.Arguments)))
            .ToArray() ?? [];

        if (string.IsNullOrWhiteSpace(content) && toolCalls.Length == 0)
        {
            throw new InvalidOperationException("OpenRouter returned an empty chat response.");
        }

        return new LlmChatResult(content, toolCalls);
    }

    private static JsonElement ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return EmptyObject;
        }

        try
        {
            return JsonDocument.Parse(arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = arguments });
        }
    }

    private static OpenRouterToolDto ToToolDto(LlmToolDefinition tool) => new()
    {
        Type = "function",
        Function = new OpenRouterFunctionDto
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = tool.Parameters
        }
    };

    private static OpenRouterToolCallDto ToToolCallDto(LlmToolCall call) => new()
    {
        Id = call.Id,
        Type = "function",
        Function = new OpenRouterFunctionCallDto
        {
            Name = call.Name,
            Arguments = call.Arguments.GetRawText()
        }
    };

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

        [JsonPropertyName("supported_parameters")]
        public List<string>? SupportedParameters { get; init; }
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

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OpenRouterToolDto>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolChoice { get; init; }

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
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OpenRouterToolCallDto>? ToolCalls { get; init; }
    }

    private sealed class OpenRouterToolDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public OpenRouterFunctionDto Function { get; init; } = new();
    }

    private sealed class OpenRouterFunctionDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; init; }
    }

    private sealed class OpenRouterToolCallDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public OpenRouterFunctionCallDto? Function { get; init; }
    }

    private sealed class OpenRouterFunctionCallDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; init; } = "{}";
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

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static object ParseJsonOrRaw(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return json; }
    }

    private static string GetErrorMessage(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? responseJson;
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? responseJson;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw provider response below.
        }

        return string.IsNullOrWhiteSpace(responseJson) ? "No error details were returned." : responseJson;
    }
}
