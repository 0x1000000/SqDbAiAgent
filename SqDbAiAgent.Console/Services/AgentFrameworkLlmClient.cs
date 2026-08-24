using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Models;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AppChatMessage = SqDbAiAgent.ConsoleApp.Models.ChatMessage;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class AgentFrameworkLlmClient(
    AgentFrameworkChatClientFactory clientFactory,
    IOptions<AppConfig> appConfig,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<OpenRouterOptions> openRouterOptions) : ILlmClient, IDisposable
{
    private readonly IChatClient _chatClient = clientFactory.Create();
    private readonly string _model = string.Equals(appConfig.Value.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase)
        ? openRouterOptions.Value.Model
        : ollamaOptions.Value.Model;

    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        clientFactory.GetAvailableModelsAsync(cancellationToken);

    public Task<LlmModelCapabilities> GetModelCapabilitiesAsync(
        string model,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlmModelCapabilities(true));

    public async Task<LlmChatResult> ChatAsync(
        string model,
        IReadOnlyList<AppChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var aiMessages = messages.Select(ConvertMessage).ToList();
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions
            {
                Effort = ToReasoningEffort(thinkLevel),
                Output = ReasoningOutput.None
            }
        };
        if (format is { } schema)
        {
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "response");
        }

        if (tools is { Count: > 0 })
        {
            options.Tools = tools
                .Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description, tool.Parameters))
                .ToList();
        }

        var response = await this._chatClient.GetResponseAsync(aiMessages, options, cancellationToken);

        var calls = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => new LlmToolCall(
                call.CallId,
                call.Name,
                JsonSerializer.SerializeToElement(call.Arguments)))
            .ToList();

        return new LlmChatResult(response.Text ?? string.Empty, calls);
    }

    public void Dispose() => this._chatClient.Dispose();

    private static ReasoningEffort ToReasoningEffort(LlmThinkLevel thinkLevel) => thinkLevel switch
    {
        LlmThinkLevel.Low => ReasoningEffort.Low,
        LlmThinkLevel.Medium => ReasoningEffort.Medium,
        LlmThinkLevel.High => ReasoningEffort.High,
        LlmThinkLevel.Enabled => ReasoningEffort.Low,
        _ => ReasoningEffort.None
    };

    private static AiChatMessage ConvertMessage(AppChatMessage message)
    {
        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new AiChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(message.ToolCallId ?? string.Empty, message.Content)]);
        }

        var role = message.Role.ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            _ => ChatRole.User
        };

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message.Content))
        {
            contents.Add(new TextContent(message.Content));
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            contents.AddRange(message.ToolCalls.Select(call => new FunctionCallContent(
                call.Id,
                call.Name,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.GetRawText()))));
        }

        return new AiChatMessage(role, contents);
    }
}
