using System.Text;
using System.Text.Json;
using SqDbAiAgent.ConsoleApp.Conversation;

namespace SqDbAiAgent.ConsoleApp.Services.Chat;

public sealed class DbChatSession(
    IConsoleOutput output,
    AppConfig appConfig,
    ILlmClient ollamaClient,
    MessageAnalyzeSession messageAnalyzeSession,
    ValidatedSqlExecutor validatedSqlExecutor,
    string schemaPrompt,
    string llmName,
    string databaseName,
    bool useNativeTools)
{
    private readonly string _agentSystemPrompt = useNativeTools
        ? BuildNativeAgentSystemPrompt(databaseName, schemaPrompt, appConfig.InvestigationEnabled)
        : BuildAgentSystemPrompt(databaseName, schemaPrompt, appConfig.InvestigationEnabled);

    private readonly IReadOnlyList<LlmToolDefinition> _tools = BuildToolDefinitions(
        appConfig.ToolScope,
        appConfig.InvestigationEnabled);

    private readonly ChatHistoryManager<NoToolsAgentResponse> _agentHistory = new(
        appConfig.MaxPromptChars,
        action => action.ToJsonString()
    );

    public async Task<bool> HandleInputAsync(string userRequest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return true;
        }

        this.WriteRequestStart();

        var messageAnalysis = await this.AnalyzeMessageAsync(userRequest.Trim(), cancellationToken);
        if (messageAnalysis is null)
        {
            this.WriteNoToolsAgentResponseFailure();
            return true;
        }

        if (useNativeTools)
        {
            return await this.HandleInputWithNativeToolsAsync(
                userRequest.Trim(),
                !messageAnalysis.Value.IsNewTopic,
                cancellationToken);
        }

        return await this.HandleInputWithStructuredActionsAsync(
            userRequest.Trim(),
            !messageAnalysis.Value.IsNewTopic,
            initialInvestigationCount: 0,
            initialInvestigationSql: null,
            cancellationToken);
    }

    private async Task<bool> HandleInputWithStructuredActionsAsync(
        string userRequest,
        bool includeHistory,
        int initialInvestigationCount,
        HashSet<string>? initialInvestigationSql,
        CancellationToken cancellationToken)
    {
        var currentAgentInput = userRequest;
        var investigationCount = initialInvestigationCount;
        var executedInvestigationSql = initialInvestigationSql
                                       ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var investigationRequired = appConfig.InvestigationEnabled
                                    && InvestigationRequestPolicy.IsExplicitlyRequested(userRequest);
        var databaseOverviewRequested = DatabaseOverviewPolicy.IsOverviewRequest(userRequest);
        var currentInputIsInvestigationResult = false;

        for (var stepIndex = 1; stepIndex <= appConfig.MaxAgentSteps; stepIndex++)
        {
            var action = await this.TryGetNoToolsAgentResponseAsync(
                currentAgentInput,
                stepIndex == 1 && includeHistory,
                cancellationToken
            );
            if (action is null)
            {
                this.WriteNoToolsAgentResponseFailure();
                return true;
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.Exit
                && !ConversationExitPolicy.IsExplicitExitRequest(userRequest))
            {
                currentAgentInput =
                    "The previous exit action was rejected because the user did not ask to stop. "
                    + "This is a supported database-domain request. Answer it from the provided schema.";
                currentInputIsInvestigationResult = true;
                continue;
            }

            if (databaseOverviewRequested
                && action.Value.ActionType == NoToolsAgentResponseType.Respond
                && DatabaseOverviewPolicy.IsUnhelpfulResponse(action.Value.Message))
            {
                action = new NoToolsAgentResponse(
                    NoToolsAgentResponseType.Respond,
                    DatabaseOverviewPolicy.BuildFallback(databaseName, schemaPrompt),
                    string.Empty);
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.RunSql
                && investigationRequired
                && investigationCount == 0)
            {
                currentAgentInput =
                    "The user explicitly requested investigation. Use investigate_sql at least once before run_sql.";
                currentInputIsInvestigationResult = true;
                continue;
            }

            if (action.Value.ActionType != NoToolsAgentResponseType.InvestigateSql)
            {
                this.AppendAgentTurn(
                    currentInputIsInvestigationResult ? userRequest.Trim() : currentAgentInput,
                    action.Value);
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.Exit)
            {
                this.WriteExit(action.Value);
                return false;
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.HandleOffTopic)
            {
                this.WriteOffTopic(action.Value);
                return true;
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.Respond)
            {
                if (investigationRequired && investigationCount == 0)
                {
                    currentAgentInput =
                        "The user explicitly requested investigation. Use investigate_sql before returning an answer.";
                    currentInputIsInvestigationResult = true;
                    continue;
                }

                if (NativeAgentResponsePolicy.RejectRenderedTableAssistantText(action.Value.Message))
                {
                    currentAgentInput =
                        "Do not render or reconstruct a table. Return a concise plain-text answer using the available evidence.";
                    continue;
                }

                this.WriteRespond(action.Value);
                return true;
            }

            if (action.Value.ActionType == NoToolsAgentResponseType.InvestigateSql)
            {
                if (!appConfig.InvestigationEnabled)
                {
                    currentAgentInput = "Investigation is disabled. Submit a final query, ask one clarification, or answer without investigating.";
                    currentInputIsInvestigationResult = true;
                    continue;
                }

                if (investigationCount >= appConfig.MaxInvestigationQueries)
                {
                    currentAgentInput =
                        $"The investigation limit of {appConfig.MaxInvestigationQueries} queries was reached. "
                        + "Submit a final query, ask one clarification, or explain that no authorized match was found.";
                    currentInputIsInvestigationResult = true;
                    continue;
                }

                if (!executedInvestigationSql.Add(NormalizeSql(action.Value.Sql)))
                {
                    currentAgentInput =
                        "This exact investigation query already ran. Do not repeat it; submit a final query, test a different concrete uncertainty, ask one clarification, or report no match.";
                    currentInputIsInvestigationResult = true;
                    continue;
                }

                investigationCount++;
                var investigationResult = await validatedSqlExecutor.InvestigateAsync(
                    userRequest,
                    action.Value.Purpose,
                    action.Value.Sql,
                    cancellationToken);
                currentAgentInput = investigationResult is null
                    ? BuildInvestigationFailureMessage(validatedSqlExecutor.LastInvestigationFailure)
                    : BuildInvestigationResultMessage(
                        action.Value.Purpose,
                        investigationResult.ApprovedSql,
                        investigationResult.RenderedTable);
                if (investigationResult is not null)
                {
                    executedInvestigationSql.Add(NormalizeSql(investigationResult.ApprovedSql));
                }
                currentInputIsInvestigationResult = true;
                continue;
            }

            var executionResult = await validatedSqlExecutor.SubmitAsync(
                userRequest,
                action.Value.Sql,
                cancellationToken);
            if (executionResult is null)
            {
                output.OutDataLine(string.Empty);
                return true;
            }

            currentAgentInput = this.BuildToolResultMessage(
                userRequest,
                executionResult.ApprovedSql,
                executionResult.RenderedTable
            );
            currentInputIsInvestigationResult = false;
        }

        if (databaseOverviewRequested)
        {
            var fallback = new NoToolsAgentResponse(
                NoToolsAgentResponseType.Respond,
                DatabaseOverviewPolicy.BuildFallback(databaseName, schemaPrompt),
                string.Empty);
            this.AppendAgentTurn(userRequest, fallback);
            this.WriteRespond(fallback);
            return true;
        }

        this.WriteAgentStepLimitReached();

        return true;
    }

    private async Task<bool> HandleInputWithNativeToolsAsync(
        string userRequest,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var messages = this.BuildNativeMessages(userRequest, includeHistory);
        var historyInput = userRequest;
        var submitSqlRequired = false;
        var investigationCount = 0;
        var executedInvestigationSql = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var investigationRequired = appConfig.InvestigationEnabled
                                    && InvestigationRequestPolicy.IsExplicitlyRequested(userRequest);
        var databaseOverviewRequested = DatabaseOverviewPolicy.IsOverviewRequest(userRequest);
        var finalQueryPrinted = false;

        for (var stepIndex = 1; stepIndex <= appConfig.MaxAgentSteps; stepIndex++)
        {
            LlmChatResult result;
            try
            {
                result = await ollamaClient.ChatAsync(
                    llmName,
                    messages,
                    thinkLevel: this.GetRetryThinkLevel(stepIndex),
                    tools: this._tools,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                this.WriteModelCallFailure("native agent step", stepIndex, ex);
                if (!LlmRetryPolicy.ShouldRetry(ex))
                {
                    throw;
                }

                continue;
            }

            if (result.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(result.Content))
                {
                    messages.Add(new ChatMessage("user", "Return a plain-text answer or call exactly one available tool."));
                    continue;
                }

                if (submitSqlRequired)
                {
                    output.OutDebugLine("Model returned assistant text after being required to call submit_sql.");
                    output.OutDebugLine(string.Empty);
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "This response is still invalid. You already attempted to answer with SQL, so you must call submit_sql now. Do not answer from memory and do not return assistant text."));
                    continue;
                }

                if (investigationRequired && investigationCount == 0)
                {
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "The user explicitly requested investigation. Call investigate_sql before returning an answer or calling submit_sql."));
                    continue;
                }

                if (NativeAgentResponsePolicy.RejectSqlAssistantText(result.Content))
                {
                    output.OutDebugLine("Model returned SQL in assistant text instead of calling submit_sql.");
                    output.OutDebugLine(string.Empty);
                    submitSqlRequired = true;
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "Your previous response was invalid because it returned SQL as assistant text. Never show proposed SQL to the user. Call submit_sql now with that SQL and do not return assistant text."));
                    continue;
                }

                if (NativeAgentResponsePolicy.RejectRenderedTableAssistantText(result.Content))
                {
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "Do not render or reconstruct a table. Return a concise plain-text answer using the available evidence."));
                    continue;
                }

                if (NativeAgentResponsePolicy.RejectPseudoToolCallAssistantText(result.Content))
                {
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "Do not print a JSON description of a tool call. Invoke exactly one advertised tool through the tool-calling interface."));
                    continue;
                }

                if (NativeAgentResponsePolicy.RejectJsonAssistantText(result.Content))
                {
                    messages.Add(new ChatMessage("assistant", result.Content));
                    messages.Add(new ChatMessage(
                        "user",
                        "Do not print JSON as assistant text. Invoke an advertised tool or return a concise plain-text answer."));
                    continue;
                }

                var response = databaseOverviewRequested
                               && DatabaseOverviewPolicy.IsUnhelpfulResponse(result.Content)
                    ? DatabaseOverviewPolicy.BuildFallback(databaseName, schemaPrompt)
                    : result.Content.Trim();
                var action = new NoToolsAgentResponse(NoToolsAgentResponseType.Respond, response, string.Empty);
                this.AppendAgentTurn(historyInput, action);
                this.WriteRespond(action);
                return true;
            }

            messages.Add(new ChatMessage("assistant", result.Content, result.ToolCalls));

            if (result.ToolCalls.Count != 1)
            {
                foreach (var conflictingCall in result.ToolCalls)
                {
                    messages.Add(BuildToolMessage(
                        conflictingCall,
                        "Rejected: call exactly one tool at a time."));
                }

                continue;
            }

            var call = result.ToolCalls[0];
            if (submitSqlRequired && !string.Equals(call.Name, "submit_sql", StringComparison.Ordinal))
            {
                messages.Add(BuildToolMessage(call, "Rejected: submit_sql is required to correct the previous SQL assistant response."));
                continue;
            }

            switch (call.Name)
            {
                case "describe_database":
                    messages.Add(BuildToolMessage(call, schemaPrompt));
                    continue;

                case "clarify_request":
                    if (!TryGetStringArgument(call.Arguments, "question", out var question))
                    {
                        messages.Add(BuildToolMessage(call, "Rejected: question must be a non-empty string."));
                        continue;
                    }

                    var clarification = new NoToolsAgentResponse(NoToolsAgentResponseType.Respond, question, string.Empty);
                    this.AppendAgentTurn(userRequest, clarification);
                    this.WriteRespond(clarification);
                    return true;

                case "finish_conversation":
                    if (!ConversationExitPolicy.IsExplicitExitRequest(userRequest))
                    {
                        messages.Add(BuildToolMessage(
                            call,
                            "Rejected: the user did not explicitly ask to end the conversation. Continue answering the current request."));
                        continue;
                    }

                    TryGetStringArgument(call.Arguments, "message", out var goodbye);
                    var exitAction = new NoToolsAgentResponse(NoToolsAgentResponseType.Exit, goodbye, string.Empty);
                    this.AppendAgentTurn(userRequest, exitAction);
                    this.WriteExit(exitAction);
                    return false;

                case "submit_sql":
                    if (investigationRequired && investigationCount == 0)
                    {
                        messages.Add(BuildToolMessage(
                            call,
                            "Rejected: the user explicitly requested investigation. Call investigate_sql at least once before submit_sql."));
                        continue;
                    }

                    if (!TryGetStringArgument(call.Arguments, "sql", out var sql))
                    {
                        messages.Add(BuildToolMessage(call, "Rejected: sql must be a non-empty string."));
                        continue;
                    }

                    submitSqlRequired = false;
                    var sqlAction = new NoToolsAgentResponse(NoToolsAgentResponseType.RunSql, string.Empty, sql);
                    this.AppendAgentTurn(historyInput, sqlAction);
                    var executionResult = await validatedSqlExecutor.SubmitAsync(
                        userRequest,
                        sql,
                        cancellationToken);
                    if (executionResult is null)
                    {
                        messages.Add(BuildToolMessage(call, "SQL was rejected or execution failed after validation and repair attempts."));
                        continue;
                    }
                    finalQueryPrinted = true;
                    var nativeToolResult = this.BuildNativeToolResultMessage(
                        userRequest,
                        executionResult.ApprovedSql,
                        executionResult.RenderedTable);
                    messages.Add(BuildToolMessage(call, nativeToolResult));
                    historyInput = this.BuildToolResultMessage(
                        userRequest,
                        executionResult.ApprovedSql,
                        executionResult.RenderedTable);
                    continue;

                case "investigate_sql":
                    if (!TryGetStringArgument(call.Arguments, "purpose", out var purpose)
                        || !TryGetStringArgument(call.Arguments, "sql", out var investigationSql))
                    {
                        messages.Add(BuildToolMessage(
                            call,
                            "Rejected: purpose and sql must both be non-empty strings."));
                        continue;
                    }

                    if (investigationCount >= appConfig.MaxInvestigationQueries)
                    {
                        messages.Add(BuildToolMessage(
                            call,
                            $"Rejected: the investigation limit of {appConfig.MaxInvestigationQueries} queries was reached. "
                            + "Submit a final query, ask one clarification, or report that no authorized match was found."));
                        continue;
                    }

                    if (!executedInvestigationSql.Add(NormalizeSql(investigationSql)))
                    {
                        messages.Add(BuildToolMessage(
                            call,
                            "Rejected: this exact investigation query already ran. Do not repeat it; submit a final query or test a different concrete uncertainty."));
                        continue;
                    }

                    investigationCount++;
                    var investigationResult = await validatedSqlExecutor.InvestigateAsync(
                        userRequest,
                        purpose,
                        investigationSql,
                        cancellationToken);
                    if (investigationResult is not null)
                    {
                        executedInvestigationSql.Add(NormalizeSql(investigationResult.ApprovedSql));
                    }
                    messages.Add(BuildToolMessage(
                        call,
                        investigationResult is null
                            ? BuildInvestigationFailureMessage(validatedSqlExecutor.LastInvestigationFailure)
                            : BuildInvestigationResultMessage(
                                purpose,
                                investigationResult.ApprovedSql,
                                investigationResult.RenderedTable)));
                    continue;

                default:
                    messages.Add(BuildToolMessage(call, $"Rejected: unknown tool '{call.Name}'. Use one of the advertised tools."));
                    continue;
            }
        }

        if (appConfig.ToolCalling == ToolCallingMode.Auto && !finalQueryPrinted)
        {
            output.OutDebugLine(
                "Native tool orchestration did not complete. Falling back to structured JSON actions.");
            output.OutDebugLine(string.Empty);
            return await this.HandleInputWithStructuredActionsAsync(
                userRequest,
                includeHistory,
                investigationCount,
                executedInvestigationSql,
                cancellationToken);
        }

        if (databaseOverviewRequested)
        {
            var fallback = new NoToolsAgentResponse(
                NoToolsAgentResponseType.Respond,
                DatabaseOverviewPolicy.BuildFallback(databaseName, schemaPrompt),
                string.Empty);
            this.AppendAgentTurn(userRequest, fallback);
            this.WriteRespond(fallback);
            return true;
        }

        this.WriteAgentStepLimitReached();
        return true;
    }

    private List<ChatMessage> BuildNativeMessages(string currentInstruction, bool includeHistory)
    {
        var messages = new List<ChatMessage> { new("system", this._agentSystemPrompt) };
        if (includeHistory)
        {
            var remainingBudget = appConfig.MaxPromptChars
                                  - this._agentSystemPrompt.Length
                                  - currentInstruction.Length
                                  - appConfig.PromptSafetyChars;
            if (remainingBudget > 0)
            {
                messages.AddRange(this._agentHistory.BuildHistory(
                    remainingBudget,
                    assistantFormatter: FormatAnalyzerAssistantMessage,
                    userFormatter: FormatAnalyzerUserMessage));
            }
        }

        messages.Add(new ChatMessage("user", currentInstruction));
        return messages;
    }

    private static ChatMessage BuildToolMessage(LlmToolCall call, string content) =>
        new("tool", content, ToolCallId: call.Id, Name: call.Name);

    private static bool TryGetStringArgument(JsonElement arguments, string name, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private async Task<MessageAnalysisResult?> AnalyzeMessageAsync(string userRequest, CancellationToken cancellationToken)
    {
        var oldMessages = this.BuildAnalyzerHistory(userRequest);
        var classification = await messageAnalyzeSession.ClassifyAsync(oldMessages, userRequest, cancellationToken);
        if (classification is null)
        {
            return null;
        }

        var newTopic = await messageAnalyzeSession.CheckNewTopicAsync(oldMessages, userRequest, cancellationToken);
        if (newTopic is null)
        {
            return null;
        }

        var analysis = new MessageAnalysisResult(
            classification.Value.Kind,
            classification.Value.Kind == MessageKind.FollowUp
                ? false
                : newTopic.Value.IsNewTopic);
        if (analysis is var parsed)
        {
            output.OutDebugLine(
                $"Message analysis: kind={parsed.Kind}, isNewTopic={parsed.IsNewTopic.ToString().ToLowerInvariant()}");
            output.OutDebugLine(string.Empty);
        }

        return analysis;
    }

    private IReadOnlyList<ChatMessage> BuildMessages(string currentInstruction, bool includeHistory)
    {
        var messages = new List<ChatMessage>
        {
            new("system", _agentSystemPrompt)
        };

        if (includeHistory)
        {
            var remainingBudget = appConfig.MaxPromptChars
                                  - _agentSystemPrompt.Length
                                  - currentInstruction.Length
                                  - appConfig.PromptSafetyChars;

            if (remainingBudget > 0)
            {
                messages.AddRange(this._agentHistory.BuildHistory(remainingBudget));
            }
        }

        messages.Add(new ChatMessage("user", currentInstruction));
        return messages;
    }

    private IReadOnlyList<ChatMessage> BuildAnalyzerHistory(string newMessage)
    {
        var availableChars = Math.Max(
            0,
            appConfig.MaxPromptChars
            - newMessage.Length
            - appConfig.PromptSafetyChars
            - 4000);

        return this._agentHistory.BuildHistory(
            availableChars,
            FormatAnalyzerUserMessage,
            FormatAnalyzerAssistantMessage);
    }

    private void AppendAgentTurn(string userRequest, NoToolsAgentResponse action)
    {
        var removedCount = this._agentHistory.Push(userRequest, action);
        if (removedCount > 0)
        {
            output.OutDebugLine($"Conversation history trimmed. Removed {removedCount} old turn(s).");
        }
    }

    private async Task<NoToolsAgentResponse?> TryGetNoToolsAgentResponseAsync(
        string currentInstruction,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var currentRetryInstruction = currentInstruction;

        for (var attempt = 1; attempt <= appConfig.MaxClassificationAttempts; attempt++)
        {
            var messages = this.BuildMessages(currentRetryInstruction, includeHistory);
            var reply = await this.TryChatJsonAsync(messages, "agent action", attempt, cancellationToken);
            if (reply is null)
            {
                if (attempt == appConfig.MaxClassificationAttempts)
                {
                    break;
                }

                currentRetryInstruction =
                    $$"""
                      Your previous response did not follow the required action contract.
                      Return exactly one JSON object with action equal to {{BuildStructuredActionList(appConfig.InvestigationEnabled)}}.
                      Include action, message, sql, and purpose.
                      Do not include markdown, code fences, comments, or extra text.

                      Latest instruction:
                      {{currentInstruction}}
                      """;
                continue;
            }

            var action = TryParseNoToolsAgentResponse(reply);
            if (action is { } parsedAction)
            {
                return parsedAction;
            }

            this.WriteInvalidActionResponse(attempt, reply);

            if (attempt == appConfig.MaxClassificationAttempts)
            {
                break;
            }

            currentRetryInstruction =
                $$"""
                  Your previous response did not follow the required action contract.
                  Return exactly one JSON object with action equal to {{BuildStructuredActionList(appConfig.InvestigationEnabled)}}.
                  Include action, message, sql, and purpose.
                  Do not include markdown, code fences, comments, or extra text.

                  Latest instruction:
                  {{currentInstruction}}
                  """;
        }

        return null;
    }

    private async Task<string?> TryChatJsonAsync(
        IReadOnlyList<ChatMessage> messages,
        string operationName,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ollamaClient.ChatAsync(
                llmName,
                messages,
                appConfig.InvestigationEnabled
                    ? NoToolsAgentResponse.JsonSchema
                    : NoToolsAgentResponse.JsonSchemaWithoutInvestigation,
                thinkLevel: this.GetRetryThinkLevel(attempt),
                cancellationToken: cancellationToken
            );
            return result.Content;
        }
        catch (Exception ex)
        {
            this.WriteModelCallFailure(operationName, attempt, ex);

            if (!LlmRetryPolicy.ShouldRetry(ex))
            {
                throw;
            }

            return null;
        }
    }

    private static NoToolsAgentResponse? TryParseNoToolsAgentResponse(string reply)
    {
        var trimmed = StripMarkdownFence(reply);
        return NoToolsAgentResponse.TryParseFromJson(trimmed, out var action)
            ? action
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

    private static string FormatAnalyzerAssistantMessage(NoToolsAgentResponse action)
    {
        return action.ActionType switch
        {
            NoToolsAgentResponseType.Respond => action.Message,
            NoToolsAgentResponseType.HandleOffTopic => action.Message,
            NoToolsAgentResponseType.Exit => action.Message,
            NoToolsAgentResponseType.RunSql => string.IsNullOrWhiteSpace(action.Sql)
                ? "The assistant ran a SQL query."
                : $"The assistant ran this SQL query:{Environment.NewLine}{action.Sql}",
            NoToolsAgentResponseType.InvestigateSql => "The assistant performed an internal investigation query.",
            _ => string.Empty
        };
    }

    private static string FormatAnalyzerUserMessage(string message)
    {
        const string toolPrefix = "The SQL tool completed for the current request.";
        if (!message.StartsWith(toolPrefix, StringComparison.Ordinal))
        {
            return message;
        }

        var builder = new StringBuilder();
        foreach (var line in message.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("You are now in result explanation mode.", StringComparison.Ordinal)
                || line.StartsWith("Check the data below and provide a very short follow-up.", StringComparison.Ordinal)
                || line.StartsWith("Answer the original user request using the visible data only.", StringComparison.Ordinal)
                || line.StartsWith("Do not ", StringComparison.Ordinal)
                || line.StartsWith("Return exactly one JSON object now.", StringComparison.Ordinal)
                || line.StartsWith("- Use action = ", StringComparison.Ordinal)
                || line.StartsWith("- For action = ", StringComparison.Ordinal))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private LlmThinkLevel GetRetryThinkLevel(int attempt)
    {
        return appConfig.Reasoning switch
        {
            LlmReasoningMode.Enabled => LlmThinkLevel.Enabled,
            LlmReasoningMode.Disabled => LlmThinkLevel.Disabled,
            _ => attempt > appConfig.ThinkAfterAttempt
                ? LlmThinkLevel.Low
                : LlmThinkLevel.Disabled
        };
    }

    private static string BuildDefaultOffTopicMessage(string databaseName, string modelMessage)
    {
        if (!string.IsNullOrWhiteSpace(modelMessage))
        {
            return modelMessage;
        }

        return
            "I can help with the connected database: explain the exposed schema and domain, suggest executable query examples, clarify returned data, or continue a database conversation. Try asking for query examples or a concrete data question.";
    }

    private string BuildToolResultMessage(
        string originalUserRequest,
        string approvedSql,
        RenderedTable renderedTable)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The SQL tool completed for the current request.");
        builder.AppendLine("You are now in result explanation mode.");
        builder.AppendLine("Check the data below and provide a very short follow-up.");
        builder.AppendLine("Answer the original user request using the visible data only.");
        builder.AppendLine("Do not introduce yourself.");
        builder.AppendLine("Do not describe your abilities.");
        builder.AppendLine("Do not provide example prompts.");
        builder.AppendLine("Assume the user does not know SQL or database internals. Answer in clear domain language.");
        builder.AppendLine("Unless the original request explicitly asks for technical details, do not mention SQL text, schema/table/column identifiers, tool calls, or validation and security implementation details.");
        builder.AppendLine("Do not print or reconstruct a table. The application already renders the table.");
        builder.AppendLine("Summarize the visible data in plain text only.");
        builder.AppendLine();
        builder.AppendLine($"Original user request: {originalUserRequest}");
        builder.AppendLine($"Approved SQL: {approvedSql}");
        builder.AppendLine($"Result rows: {renderedTable.TotalRows}");
        builder.AppendLine(
            $"Visible result shape: {renderedTable.ShownRows} row(s), {renderedTable.ShownColumns} column(s), {renderedTable.ShownCells} cell(s)."
        );
        if (renderedTable.Truncated)
        {
            builder.AppendLine(
                $"The result was truncated to stay within the {appConfig.MaxAgentVisibleCells}-cell visibility budget."
            );
        }

        builder.AppendLine();
        builder.AppendLine("Visible result grid:");
        builder.AppendLine(renderedTable.Markdown);
        builder.AppendLine();
        builder.AppendLine("Return exactly one JSON object now.");
        builder.AppendLine("- Use action = \"respond\" if the result already answers the user.");
        builder.AppendLine("- For action = \"respond\", message must be non-empty, very short, and plain text only.");
        builder.AppendLine("- Use action = \"run_sql\" only if another read-only SQL query is still needed.");
        if (appConfig.InvestigationEnabled)
        {
            builder.AppendLine("- Use action = \"investigate_sql\" only for a small internal probe needed to diagnose an uncertain or zero-row result.");
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildNativeToolResultMessage(
        string originalUserRequest,
        string approvedSql,
        RenderedTable renderedTable)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The validated SQL query completed successfully.");
        builder.AppendLine($"Original user request: {originalUserRequest}");
        builder.AppendLine($"Approved SQL: {approvedSql}");
        builder.AppendLine($"Result rows: {renderedTable.TotalRows}");
        builder.AppendLine(
            $"Visible result shape: {renderedTable.ShownRows} row(s), {renderedTable.ShownColumns} column(s), {renderedTable.ShownCells} cell(s).");
        if (renderedTable.Truncated)
        {
            builder.AppendLine($"The result was truncated to {appConfig.MaxAgentVisibleCells} visible cells.");
        }

        builder.AppendLine("Visible result grid:");
        builder.AppendLine(renderedTable.Markdown);
        builder.AppendLine("Summarize the result briefly in plain text. Do not render or reconstruct a table.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildInvestigationResultMessage(
        string purpose,
        string approvedSql,
        RenderedTable renderedTable)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Internal investigation completed. Do not show this grid to the user.");
        builder.AppendLine($"Purpose: {purpose}");
        builder.AppendLine($"Approved SQL: {approvedSql}");
        builder.AppendLine($"Result rows: {renderedTable.TotalRows}");
        builder.AppendLine(
            $"Visible investigation shape: {renderedTable.ShownRows} row(s), "
            + $"{renderedTable.ShownColumns} column(s), {renderedTable.ShownCells} cell(s).");
        if (renderedTable.Truncated)
        {
            builder.AppendLine("The investigation result was truncated by its internal visibility budget.");
        }

        builder.AppendLine("Internal result grid:");
        builder.AppendLine(renderedTable.Markdown);
        builder.AppendLine(
            "Use this evidence to submit a final query, investigate one remaining uncertainty, "
            + "ask one clarification, or report that no authorized match was found.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildInvestigationFailureMessage(string? failure) =>
        "Investigation was rejected or failed after validation and repair. "
        + (string.IsNullOrWhiteSpace(failure) ? string.Empty : $"Reason: {failure} ")
        + "Correct the probe within the remaining investigation budget or choose a final outcome.";

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        ToolScope scope,
        bool investigationEnabled)
    {
        var tools = new List<LlmToolDefinition>
        {
            new(
                "submit_sql",
                "Submit one read-only Microsoft SQL Server query for application validation, security filtering, and execution.",
                JsonDocument.Parse(
                    """
                    {"type":"object","properties":{"sql":{"type":"string","description":"A complete self-contained read-only T-SQL query."}},"required":["sql"],"additionalProperties":false}
                    """).RootElement.Clone())
        };

        if (investigationEnabled)
        {
            tools.Add(new LlmToolDefinition(
                "investigate_sql",
                "Run one small internal read-only SQL probe through full validation and security. Results are evidence for planning and are not shown as the final answer.",
                JsonDocument.Parse(
                    """
                    {"type":"object","properties":{"purpose":{"type":"string","description":"The specific uncertainty this probe will resolve."},"sql":{"type":"string","description":"A narrow self-contained read-only T-SQL probe using TOP, DISTINCT, an aggregate, or an existence check."}},"required":["purpose","sql"],"additionalProperties":false}
                    """).RootElement.Clone()));
        }

        if (scope == ToolScope.Full)
        {
            tools.AddRange(
            [
                new LlmToolDefinition(
                    "describe_database",
                    "Retrieve the complete allowed database schema, relationships, and SQL guidance.",
                    JsonDocument.Parse(
                        """
                        {"type":"object","properties":{},"additionalProperties":false}
                        """).RootElement.Clone()),
                new LlmToolDefinition(
                    "clarify_request",
                    "Ask the user one concise clarification question before attempting SQL.",
                    JsonDocument.Parse(
                        """
                        {"type":"object","properties":{"question":{"type":"string"}},"required":["question"],"additionalProperties":false}
                        """).RootElement.Clone()),
                new LlmToolDefinition(
                    "finish_conversation",
                    "End the conversation when the user says goodbye or explicitly asks to stop.",
                    JsonDocument.Parse(
                        """
                        {"type":"object","properties":{"message":{"type":"string"}},"additionalProperties":false}
                        """).RootElement.Clone())
            ]);
        }

        return tools;
    }

    private static string BuildNativeAgentSystemPrompt(
        string databaseName,
        string schemaPrompt,
        bool investigationEnabled) =>
        $$"""
          You are an assistant for the connected database.
          Answer supported informational requests directly in concise plain text.
          For concrete data requests, call submit_sql with one read-only Microsoft SQL Server query proposal.
          {{BuildNativeInvestigationPrompt(investigationEnabled)}}
          Use describe_database when detailed schema information is needed, clarify_request for one necessary clarification, and finish_conversation only for goodbye or stop requests.
          Call exactly one tool at a time. Never claim that a tool succeeded until its result is returned.

          Rules:
          - Stay within database/domain information, query examples, concrete data requests, refinements, and returned-result explanations.
          - Assume the user does not know SQL or database internals. Answer in clear domain language.
          - Unless the user explicitly requests technical details, do not expose SQL text, schema/table/column identifiers, tool names or calls, or validation and security implementation details.
          - Questions about what this database contains, represents, or is used for are supported. Answer them from the schema with a useful domain overview.
          - A database overview should name the main entity groups visible in the schema and what workflows their relationships suggest; do not merely say that you are ready to help.
          - Briefly redirect unrelated requests back to database topics.
          - Use only the schema below and never invent tables, columns, relationships, or business filters.
          - The schema below is complete for your task. Never query INFORMATION_SCHEMA, sys catalog views, system tables, metadata functions, or any other source to discover schema.
          - Never use submit_sql or investigate_sql to inspect table names, column names, keys, relationships, database metadata, or connectivity.
          - SQL must be self-contained, read-only SQL Server T-SQL with exact schema-qualified names.
          - Never return proposed SQL as assistant text, Markdown, or a fenced code block.
          - SQL intended to answer a data request must appear only in the sql argument of a submit_sql tool call.
          - Never use LIMIT, RETURNING, markdown fences, comments, placeholders, variables, or parameters in SQL.
          - Prefer simple SELECT, JOIN, WHERE, GROUP BY, and ORDER BY constructs.
          - Never render or reconstruct a table in assistant text. The application renders tables; summarize visible results only.
          - A short confirmation after a proposed option normally accepts the first option and should continue toward execution.

          Initial allowed database schema:
          {{schemaPrompt}}
          """;

    private static string BuildAgentSystemPrompt(
        string databaseName,
        string schemaPrompt,
        bool investigationEnabled) =>
        $$"""
         You are an assistant for the connected database.
         Return exactly one JSON object that matches the required action schema.

         Use these exact property names:
         - action
         - message
         - sql
         - purpose
         Do not use the property name "actionType".

         Allowed actions:
         - ""respond"": answer in natural language
         - ""run_sql"": ask the SQL tool to execute one read-only SQL query
         {{BuildStructuredInvestigationAction(investigationEnabled)}}
         - ""handle_offtopic"": the message is outside supported topics
         - ""exit"": the user wants to stop or say goodbye

         Supported topics:
         - database/domain information
         - query possibilities and example prompts
         - concrete data requests and query refinements
         - clarifying or summarizing returned results
         - greetings and goodbyes

         Everything else is off-topic.

         Rules:
          - Assume the user does not know SQL or database internals. Write messages in clear domain language.
          - Unless the user explicitly requests technical details, never put SQL text, schema/table/column identifiers, tool/action names, or validation and security implementation details in the message field.
          - Use only the schema below and the current conversation. Never use remembered demo schemas or generic sample databases.
         - The schema below is complete for your task. Never query INFORMATION_SCHEMA, sys catalog views, system tables, metadata functions, or any other source to discover schema.
         - Never use "run_sql" or "investigate_sql" to inspect table names, column names, keys, relationships, database metadata, or connectivity.
         - Never say you are an OpenAI llmName, a general assistant, or that you cannot access the database.
         - Questions about what this database contains, represents, or is used for are supported database-description requests. Answer them from the schema rather than refusing.
         - A database overview must name the main entity groups visible in the schema and what workflows their relationships suggest; do not merely say that you are ready to help.
         - For greetings: use ""respond"" with a short introduction, your abilities, and 5-10 example prompts.
         - For help/capabilities/example-prompt requests: use ""respond"" with a real list of 5-10 example prompts.
         - For requests like ""most common prompts"", ""example prompts"", or ""what can I ask?"", generate example prompts from the schema. Do not talk about prompt history, telemetry, or lack of usage analytics.
         - For database-description requests: use ""respond"" with a concrete domain summary only.
         - For concrete data requests: use ""run_sql"" after resolving any uncertainty that requires investigation.
         - If the user explicitly requests investigation, or a user-provided literal is not known to match an exact stored value, use ""investigate_sql"" before ""run_sql"".
         {{BuildStructuredInvestigationPrompt(investigationEnabled)}}
         - If the user gives a short confirmation such as ""yes"", ""ok"", ""okay"", ""sure"", ""do it"", or ""proceed"" after you proposed a clarification variant or query path, treat that as agreement with the first proposed variant and most likely continue with ""run_sql"".
         - For ambiguous business terms or missing context: use ""respond"" with one short clarification question.
         - If the request appears too complex to answer reliably with one safe read-only query, use ""respond"" to say that clearly and suggest 1-3 simpler ways to break the problem down.
         - For goodbyes or stop requests: use ""exit"".
         - For unrelated topics such as jokes, trivia, politics, weather, life advice, or casual chat beyond greetings/goodbyes: use ""handle_offtopic"".
         - For ""handle_offtopic"", keep the message short and redirect back to database topics.
         - After a SQL tool result: answer only about that result. No greeting. No abilities. No example prompts.
         - If no rows were returned and the intent still seems valid: explain that and ask one short clarification question.
         - If the visible result already answers the request clearly, prefer a concise direct answer and do not add an extra follow-up question.
         - Never render or reconstruct a table yourself in the message field.
         - Never output markdown tables, pipe grids, column headers, or row dumps in the message field.
         - Table rendering is handled only by the application, not by you.
         - Never repeat or reconstruct table rows from memory or history. Summarize them in plain text instead.

         Rules for ""run_sql"":
         - Put the whole SQL query in the sql field and leave message empty.
         - Use only Microsoft SQL Server T-SQL.
         - No LIMIT, RETURNING, markdown, comments, placeholders, variables, or parameters.
         - For straightforward aggregate or ranking requests, prefer a simple SELECT with JOIN, WHERE, GROUP BY, and ORDER BY instead of CTEs, window functions, or helper subqueries.
         - Use only listed tables and columns.
         - Use exact schema-qualified table names.
         - Prefer the smallest query that answers the request.
         - Do not join extra tables unless they are needed for requested fields, filters, grouping, or sorting.
         - Do not invent extra business filters such as completed, active, shipped, paid, or cancelled unless the user explicitly asks for them or the schema clearly requires them.
         - When joining related tables, prefer the foreign-key relationship shown by the schema, not a guess based on similarly named primary keys.
         - For a simple entity list such as recent orders, recent customers, or active branches, prefer one row per main entity.
         - For relative dates like today, this month, this year, last month, or last year, use SQL Server current-date functions instead of hardcoded dates.
         - For grouped time-period requests such as by month, by week, by quarter, or by year, prefer one grouped query instead of many UNION ALL branches.
         - For month-name output, prefer DATENAME(MONTH, <date>) as the label and also include MONTH(<date>) or a similar numeric sort key so results stay in calendar order.
         - For grouped time labels, prefer a simple derived table or repeated GROUP BY expressions instead of nested wrapper queries with generated month rows.
         - For "this year", prefer a start-of-year boundary such as DATEFROMPARTS(YEAR(GETDATE()), 1, 1) and an exclusive upper bound one year later.
         - If a textual month label is needed and direct date-name functions are not parser-friendly, prefer a CASE expression on a numeric month bucket in a derived table.
         - Prefer inline date boundaries in the WHERE clause instead of separate scalar CTEs such as YearStart or YearEnd.
         - Do not use SQL for help, identity, off-topic, or prompt-example requests.

         {{BuildStructuredInvestigationRules(investigationEnabled)}}

         Examples:
         - greeting -> {"action":"respond","message":"...","sql":"","purpose":""}
         - help/examples -> {"action":"respond","message":"...","sql":"","purpose":""}
         - database description -> {"action":"respond","message":"...","sql":"","purpose":""}
         - query request -> {"action":"run_sql","message":"","sql":"SELECT ...","purpose":""}
         {{BuildStructuredInvestigationExample(investigationEnabled)}}
         - ambiguous follow-up -> {"action":"respond","message":"... ?","sql":"","purpose":""}
         - overly complex analytical request -> {"action":"respond","message":"...","sql":"","purpose":""}
         - unrelated request -> {"action":"handle_offtopic","message":"...","sql":"","purpose":""}
         - goodbye -> {"action":"exit","message":"...","sql":"","purpose":""}
         - SQL tool result with rows -> {"action":"respond","message":"...","sql":"","purpose":""}
         - SQL tool result with no rows -> {"action":"investigate_sql","message":"","sql":"SELECT TOP (20) ...","purpose":"Determine which requested filter has no matching stored value"}

         Database tables:{{Environment.NewLine}}{{schemaPrompt}}
         """;

    private static string BuildStructuredActionList(bool investigationEnabled) =>
        investigationEnabled
            ? "\"respond\", \"run_sql\", \"investigate_sql\", \"handle_offtopic\", or \"exit\""
            : "\"respond\", \"run_sql\", \"handle_offtopic\", or \"exit\"";

    private static string BuildNativeInvestigationPrompt(bool investigationEnabled) =>
        investigationEnabled
            ? """
              Use investigate_sql only for a small internal evidence query when a literal, stored value, date range, null pattern, filter, or zero-row result is uncertain.
              If the user explicitly requests investigation, or a user-provided literal is not known to match an exact stored value, call investigate_sql before submit_sql.
              Investigation is internal: use TOP no greater than the configured investigation visibility budget, or return a single aggregate value; never use SELECT * or a broad scan.
              Useful cases include uncertain spelling/casing/codes, unknown status or category values, date boundaries, null frequency, and finding which predicate causes zero rows.
              Every investigation query passes the same validation, allow-list, read-only, and user-security flow as final SQL.
              Row-returning probes must select explicit columns and use a selective WHERE clause or SELECT DISTINCT.
              For an uncertain literal, select only its candidate column with a pattern such as SELECT DISTINCT TOP (20) [Name] ... WHERE [Name] LIKE '%stable fragment%'.
              A literal-resolution probe must select only the candidate value and stable identifier columns. Never expand to all entity columns.
              Do not investigate prerequisites, relationships, counts, connectivity, or unrelated tables unless the user requested those facts.
              Never query INFORMATION_SCHEMA, sys catalog views, system tables, or metadata functions.
              The purpose must name the exact literal or filter being resolved; never use investigation to explore table structure or sample records.
              After investigation, submit a final query, answer from verified evidence, ask one clarification, or report that no authorized matching data was found.
              For entity lookup, list, count, or report requests, investigation evidence normally leads to submit_sql rather than a direct answer.
              Never reproduce an investigation grid in assistant text.
              """
            : "Investigation is disabled; do not attempt internal evidence queries.";

    private static string BuildStructuredInvestigationAction(bool investigationEnabled) =>
        investigationEnabled
            ? "- \"investigate_sql\": run one small internal evidence query before deciding the final answer"
            : string.Empty;

    private static string BuildStructuredInvestigationPrompt(bool investigationEnabled) =>
        investigationEnabled
            ? """
              - Use "investigate_sql" only when a literal, stored value, date range, null pattern, filter, or zero-row result is uncertain.
              - If the user explicitly requests investigation, or a user-provided literal is not known to match an exact stored value, "investigate_sql" is required before "run_sql".
              - Useful investigation cases include uncertain spelling/casing/codes, unknown status or category values, date boundaries, null frequency, and finding which predicate causes zero rows.
              - Investigation is internal. Never reproduce its grid in a response.
              - For a literal-resolution probe, select only the candidate value and stable identifier columns. Never expand to all entity columns.
              - Do not investigate prerequisites, relationships, counts, connectivity, or unrelated tables unless requested.
              - Never query INFORMATION_SCHEMA, sys catalog views, system tables, or metadata functions.
              - After investigation, use "run_sql", answer from verified evidence, ask one clarification, or report that no authorized matching data was found.
              """
            : "- Investigation is disabled; never use \"investigate_sql\".";

    private static string BuildStructuredInvestigationRules(bool investigationEnabled) =>
        investigationEnabled
            ? """
              Rules for "investigate_sql":
              - Put the uncertainty being tested in purpose, put the probe in sql, and leave message empty.
              - Use TOP no greater than the configured investigation visibility budget, or return a single aggregate value.
              - Never use SELECT *, broad scans, or investigation merely to browse data.
              - Never query INFORMATION_SCHEMA, sys catalog views, system tables, or metadata functions.
              - Every probe passes the same validation, allow-list, read-only, and user-security flow as final SQL.
              - Row-returning probes must select explicit columns and use a selective WHERE clause or SELECT DISTINCT.
              - For an uncertain literal, select only its candidate column with a pattern like SELECT DISTINCT TOP (20) [Name] ... WHERE [Name] LIKE '%stable fragment%'.
              - The purpose must name the exact literal or filter being resolved; never investigate table structure or sample records.
              - For entity lookup, list, count, or report requests, investigation evidence normally leads to "run_sql" rather than a direct answer.
              """
            : string.Empty;

    private static string BuildStructuredInvestigationExample(bool investigationEnabled) =>
        investigationEnabled
            ? """- uncertain literal -> {"action":"investigate_sql","message":"","sql":"SELECT DISTINCT TOP (20) ...","purpose":"Find the stored values closest to the user's literal"}"""
            : string.Empty;

    private void WriteRequestStart()
    {
        output.OutDebugLine(string.Empty);
        output.OutDebugLine($"Sending request to the LLM ({llmName})...");
        output.OutDebugLine(string.Empty);
    }

    private void WriteNoToolsAgentResponseFailure()
    {
        output.OutErrorLine("Could not obtain a valid agent action. Please try another request.");
        output.OutDebugLine(string.Empty);
    }

    private void WriteAgentStepLimitReached()
    {
        output.OutErrorLine($"The agent did not reach a final response within {appConfig.MaxAgentSteps} steps.");
        output.OutDataLine(string.Empty);
    }

    private void WriteExit(NoToolsAgentResponse action)
    {
        output.OutDataLine(string.IsNullOrWhiteSpace(action.Message) ? "Goodbye." : action.Message);
        output.OutDataLine(string.Empty);
    }

    private void WriteOffTopic(NoToolsAgentResponse action)
    {
        output.OutErrorLine(BuildDefaultOffTopicMessage(databaseName, action.Message));
        output.OutDataLine(string.Empty);
    }

    private void WriteRespond(NoToolsAgentResponse action)
    {
        output.OutDataLine(action.Message);
        output.OutDataLine(string.Empty);
    }

    private void WriteInvalidActionResponse(int attempt, string reply)
    {
        output.OutDebugLine($"Model returned invalid or unsupported action JSON on attempt {attempt}.");
        output.OutDebugLine("Raw llmName response:");
        output.OutDebugLine(reply);
        output.OutDebugLine(string.Empty);
    }

    private void WriteModelCallFailure(string operationName, int attempt, Exception ex)
    {
        output.OutDebugLine($"Model call failed during {operationName} on attempt {attempt}: {ex.Message}");
        output.OutDebugLine(string.Empty);
    }

}
