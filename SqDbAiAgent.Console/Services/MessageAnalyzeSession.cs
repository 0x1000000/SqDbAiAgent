using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class MessageAnalyzeSession : IMessageAnalyzeSession
{
    private readonly AppConfig _appConfig;
    private readonly ILlmClient _llmClient;
    private readonly IConsoleOutput _output;
    private readonly string _llmName;
    private readonly string _classificationSystemPrompt;
    private readonly string _newTopicSystemPrompt;

    public MessageAnalyzeSession(
        ILlmClient llmClient,
        IConsoleOutput output,
        AppConfig appConfig,
        string llmName,
        IReadOnlyList<TableBase> publicTables,
        string schemaPrompt,
        string databaseName)
    {
        this._appConfig = appConfig;
        this._llmClient = llmClient;
        this._output = output;
        this._llmName = llmName;
        this._classificationSystemPrompt = BuildClassificationSystemPrompt(databaseName, schemaPrompt);
        this._newTopicSystemPrompt = BuildNewTopicSystemPrompt(databaseName, schemaPrompt);
    }

    public async Task<MessageClassificationResult?> ClassifyAsync(
        IReadOnlyList<ChatMessage> oldMessages,
        string newMessage,
        CancellationToken cancellationToken = default)
    {
        var currentInstruction = BuildClassificationInstruction(oldMessages, newMessage);

        for (var attempt = 1; attempt <= this._appConfig.MaxClassificationAttempts; attempt++)
        {
            var reply = await this.TryChatJsonAsync(
                this._classificationSystemPrompt,
                currentInstruction,
                MessageClassificationResult.JsonSchema,
                "message classification",
                attempt,
                cancellationToken);
            if (reply is null)
            {
                continue;
            }

            var classification = TryParseClassification(reply);
            if (classification is { } parsed)
            {
                return parsed;
            }

            this._output.OutDebugLine($"Model returned invalid message classification JSON on attempt {attempt}.");
            this._output.OutDebugLine("Raw model response:");
            this._output.OutDebugLine(reply);
            this._output.OutDebugLine(string.Empty);

            currentInstruction =
                $$"""
                  Your previous response did not follow the required message-classification contract.
                  Return exactly one JSON object with:
                  - kind = "general_information", "off_topic", "request", or "follow_up"
                  Do not include markdown, code fences, comments, or extra text.

                  Classify this message now:
                  {{BuildClassificationInstruction(oldMessages, newMessage)}}
                  """;
        }

        return null;
    }

    public async Task<NewTopicCheckResult?> CheckNewTopicAsync(
        IReadOnlyList<ChatMessage> oldMessages,
        string newMessage,
        CancellationToken cancellationToken = default)
    {
        var currentInstruction = BuildNewTopicInstruction(oldMessages, newMessage);

        for (var attempt = 1; attempt <= this._appConfig.MaxClassificationAttempts; attempt++)
        {
            var reply = await this.TryChatJsonAsync(
                this._newTopicSystemPrompt,
                currentInstruction,
                NewTopicCheckResult.JsonSchema,
                "new-topic detection",
                attempt,
                cancellationToken);
            if (reply is null)
            {
                continue;
            }

            var result = TryParseNewTopicCheck(reply);
            if (result is { } parsed)
            {
                return parsed;
            }

            this._output.OutDebugLine($"Model returned invalid new-topic JSON on attempt {attempt}.");
            this._output.OutDebugLine("Raw model response:");
            this._output.OutDebugLine(reply);
            this._output.OutDebugLine(string.Empty);

            currentInstruction =
                $$"""
                  Your previous response did not follow the required new-topic contract.
                  Return exactly one JSON object with:
                  - isNewTopic = true or false
                  Do not include markdown, code fences, comments, or extra text.

                  Check this message now:
                  {{BuildNewTopicInstruction(oldMessages, newMessage)}}
                  """;
        }

        return null;
    }

    private async Task<string?> TryChatJsonAsync(
        string systemPrompt,
        string instruction,
        System.Text.Json.JsonElement format,
        string operationName,
        int attempt,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new("system", systemPrompt),
            new("user", instruction)
        };

        try
        {
            return await this._llmClient.ChatAsync(
                this._llmName,
                messages,
                format,
                thinkLevel: this.GetRetryThinkLevel(attempt),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            this._output.OutDebugLine($"Model call failed during {operationName} on attempt {attempt}: {ex.Message}");
            this._output.OutDebugLine(string.Empty);

            if (!LlmRetryPolicy.ShouldRetry(ex))
            {
                throw;
            }

            return null;
        }
    }

    private static string BuildClassificationSystemPrompt(string databaseName, string schemaPrompt) =>
        $$"""
         You are the message classifier for the {{databaseName}} database assistant.
         Return exactly one complete JSON object:
         {"kind":"general_information"}

         Allowed kind values:
         - general_information = greetings, goodbyes, schema/domain/help/example-prompt discussion
         - off_topic = unrelated to the database assistant
         - request = a fresh concrete data request
         - follow_up = refinement or continuation of the current topic or prior result

         Return only the JSON object.
         No extra fields.
         Do not return classification, actionType, message, sql, reason, or reasoning.
         If there is recent chat context, short refinement requests such as add, include, show also, only, use X instead, sort, filter, and explain this result should usually be follow_up, not request.
         If the user gives a short confirmation such as yes, ok, okay, sure, do it, or proceed after the assistant proposed a query shape or clarification variant, classify it as follow_up.

         Use only the schema below and the provided recent chat context.

         Database tables:
         {{schemaPrompt}}
         """;

    private static string BuildNewTopicSystemPrompt(string databaseName, string schemaPrompt) =>
        $$"""
         You are the topic-continuity checker for the {{databaseName}} database assistant.
         Return exactly one complete JSON object:
         {"isNewTopic":true}

         Meaning:
         - isNewTopic = false when the new message clearly continues, refines, clarifies, or corrects the current topic
         - isNewTopic = true when the new message starts a different topic

         Return only the JSON object.
         No extra fields.
         Do not return kind, classification, actionType, message, sql, reason, or reasoning.
         If there is recent chat context, short refinement requests such as add, include, show also, only, use X instead, sort, filter, and explain this result should usually be false.
         If the user gives a short confirmation such as yes, ok, okay, sure, do it, or proceed after the assistant proposed a query shape or clarification variant, isNewTopic should usually be false.

         Use only the schema below and the provided recent chat context.

         Database tables:
         {{schemaPrompt}}
         """;

    private static string BuildClassificationInstruction(IReadOnlyList<ChatMessage> oldMessages, string newMessage)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Recent chat context:");
        AppendMessages(builder, oldMessages);
        builder.AppendLine();
        builder.AppendLine("Classify this new user message:");
        builder.AppendLine(newMessage);
        return builder.ToString().TrimEnd();
    }

    private static string BuildNewTopicInstruction(IReadOnlyList<ChatMessage> oldMessages, string newMessage)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Recent chat context:");
        AppendMessages(builder, oldMessages);
        builder.AppendLine();
        builder.AppendLine("Does this new user message start a new topic?");
        builder.AppendLine(newMessage);
        return builder.ToString().TrimEnd();
    }

    private static void AppendMessages(System.Text.StringBuilder builder, IReadOnlyList<ChatMessage> oldMessages)
    {
        if (oldMessages.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var message in oldMessages)
        {
            builder.Append(message.Role);
            builder.Append(": ");
            builder.AppendLine(message.Content);
        }
    }

    private static MessageClassificationResult? TryParseClassification(string reply)
    {
        var trimmed = StripMarkdownFence(reply);
        return MessageClassificationResult.TryParseFromJson(trimmed, out var result)
            ? result
            : null;
    }

    private static NewTopicCheckResult? TryParseNewTopicCheck(string reply)
    {
        var trimmed = StripMarkdownFence(reply);
        return NewTopicCheckResult.TryParseFromJson(trimmed, out var result)
            ? result
            : null;
    }

    private static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine >= 0)
        {
            trimmed = trimmed[(firstNewLine + 1)..];
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            trimmed = trimmed[..closingFence];
        }

        return trimmed.Trim();
    }

    private LlmThinkLevel GetRetryThinkLevel(int attempt)
    {
        return this._appConfig.Reasoning switch
        {
            LlmReasoningMode.Enabled => LlmThinkLevel.Enabled,
            LlmReasoningMode.Disabled => LlmThinkLevel.Disabled,
            _ => attempt > this._appConfig.ThinkAfterAttempt
                ? LlmThinkLevel.Low
                : LlmThinkLevel.Disabled
        };
    }
}
