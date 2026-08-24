using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using AppConfig = SqDbAiAgent.ConsoleApp.Models.AppConfig;
using LlmReasoningMode = SqDbAiAgent.ConsoleApp.Models.LlmReasoningMode;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class InteractionLoggingChatClient(
    IChatClient innerClient,
    ILlmInteractionLogger logger,
    AppConfig appConfig) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        await logger.LogAsync(
            $"{DateTimeOffset.UtcNow:O} [MicrosoftAgentFramework request]{Environment.NewLine}" +
            JsonSerializer.Serialize(ProjectMessages(materializedMessages)),
            cancellationToken);

        var response = await base.GetResponseAsync(
            materializedMessages,
            ApplyDefaultReasoning(options),
            cancellationToken);
        await logger.LogAsync(
            $"{DateTimeOffset.UtcNow:O} [MicrosoftAgentFramework response]{Environment.NewLine}" +
            JsonSerializer.Serialize(ProjectMessages(response.Messages)),
            cancellationToken);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materializedMessages = messages.ToList();
        await logger.LogAsync(
            $"{DateTimeOffset.UtcNow:O} [MicrosoftAgentFramework streaming request]{Environment.NewLine}" +
            JsonSerializer.Serialize(ProjectMessages(materializedMessages)),
            cancellationToken);

        await foreach (var update in base.GetStreamingResponseAsync(
                           materializedMessages,
                           ApplyDefaultReasoning(options),
                           cancellationToken))
        {
            yield return update;
        }
    }

    private ChatOptions ApplyDefaultReasoning(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.Reasoning ??= new ReasoningOptions
        {
            Effort = appConfig.Reasoning == LlmReasoningMode.Enabled
                ? ReasoningEffort.Low
                : ReasoningEffort.None,
            Output = ReasoningOutput.None
        };
        return options;
    }

    private static object ProjectMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(message => new
        {
            role = message.Role.Value,
            text = message.Text,
            contents = message.Contents.Select(content => content switch
            {
                FunctionCallContent call => new { type = "tool_call", id = call.CallId, name = call.Name, value = (object?)call.Arguments },
                FunctionResultContent result => new { type = "tool_result", id = result.CallId, name = string.Empty, value = result.Result },
                _ => new { type = content.GetType().Name, id = string.Empty, name = string.Empty, value = (object?)content.ToString() }
            })
        });
}
