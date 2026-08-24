using System.Text;
using System.Text.Json;
using SqDbAiAgent.ConsoleApp.Conversation;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class DbChatSession(
    IConsoleOutput output,
    AppConfig appConfig,
    ILlmClient ollamaClient,
    IMessageAnalyzeSession messageAnalyzeSession,
    ValidatedSqlExecutor validatedSqlExecutor,
    string schemaPrompt,
    string llmName,
    string databaseName,
    bool useNativeTools) : IDbChatSession
{
    private readonly string _agentSystemPrompt = useNativeTools
        ? BuildNativeAgentSystemPrompt(databaseName, schemaPrompt)
        : BuildAgentSystemPrompt(databaseName, schemaPrompt);

    private readonly IReadOnlyList<LlmToolDefinition> _tools = BuildToolDefinitions(appConfig.ToolScope);

    private readonly ChatHistoryManager<AgentAction> _agentHistory = new(
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

        var currentAgentInput = userRequest.Trim();
        var messageAnalysis = await this.AnalyzeMessageAsync(currentAgentInput, cancellationToken);
        if (messageAnalysis is null)
        {
            this.WriteAgentActionFailure();
            return true;
        }

        if (useNativeTools)
        {
            return await this.HandleInputWithNativeToolsAsync(
                userRequest.Trim(),
                !messageAnalysis.Value.IsNewTopic,
                cancellationToken);
        }

        for (var stepIndex = 1; stepIndex <= appConfig.MaxAgentSteps; stepIndex++)
        {
            var includeHistory = stepIndex == 1 && !messageAnalysis.Value.IsNewTopic;
            var action = await this.TryGetAgentActionAsync(
                currentAgentInput,
                includeHistory,
                cancellationToken
            );
            if (action is null)
            {
                this.WriteAgentActionFailure();
                return true;
            }

            this.AppendAgentTurn(currentAgentInput, action.Value);

            if (action.Value.ActionType == AgentActionType.Exit)
            {
                this.WriteExit(action.Value);
                return false;
            }

            if (action.Value.ActionType == AgentActionType.HandleOffTopic)
            {
                this.WriteOffTopic(action.Value);
                return true;
            }

            if (action.Value.ActionType == AgentActionType.Respond)
            {
                this.WriteRespond(action.Value);
                return true;
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

                var action = new AgentAction(AgentActionType.Respond, result.Content.Trim(), string.Empty);
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

                    var clarification = new AgentAction(AgentActionType.Respond, question, string.Empty);
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
                    var exitAction = new AgentAction(AgentActionType.Exit, goodbye, string.Empty);
                    this.AppendAgentTurn(userRequest, exitAction);
                    this.WriteExit(exitAction);
                    return false;

                case "submit_sql":
                    if (!TryGetStringArgument(call.Arguments, "sql", out var sql))
                    {
                        messages.Add(BuildToolMessage(call, "Rejected: sql must be a non-empty string."));
                        continue;
                    }

                    submitSqlRequired = false;
                    var sqlAction = new AgentAction(AgentActionType.RunSql, string.Empty, sql);
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

                default:
                    messages.Add(BuildToolMessage(call, $"Rejected: unknown tool '{call.Name}'. Use one of the advertised tools."));
                    continue;
            }
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

    private void AppendAgentTurn(string userRequest, AgentAction action)
    {
        var removedCount = this._agentHistory.Push(userRequest, action);
        if (removedCount > 0)
        {
            output.OutDebugLine($"Conversation history trimmed. Removed {removedCount} old turn(s).");
        }
    }

    private async Task<AgentAction?> TryGetAgentActionAsync(
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
                      Return exactly one JSON object with action equal to "respond", "run_sql", "handle_offtopic", or "exit".
                      Do not include markdown, code fences, comments, or extra text.

                      Latest instruction:
                      {{currentInstruction}}
                      """;
                continue;
            }

            var action = TryParseAgentAction(reply);
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
                  Return exactly one JSON object with action equal to "respond", "run_sql", "handle_offtopic", or "exit".
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
                AgentAction.JsonSchema,
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

    private static AgentAction? TryParseAgentAction(string reply)
    {
        var trimmed = StripMarkdownFence(reply);
        return AgentAction.TryParseFromJson(trimmed, out var action)
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

    private static string FormatAnalyzerAssistantMessage(AgentAction action)
    {
        return action.ActionType switch
        {
            AgentActionType.Respond => action.Message,
            AgentActionType.HandleOffTopic => action.Message,
            AgentActionType.Exit => action.Message,
            AgentActionType.RunSql => string.IsNullOrWhiteSpace(action.Sql)
                ? "The assistant ran a SQL query."
                : $"The assistant ran this SQL query:{Environment.NewLine}{action.Sql}",
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
            $"I can help with the {databaseName} database: explain the schema and domain, suggest executable query examples, clarify returned data, or continue a database conversation. Try asking for query examples or a concrete data question.";
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

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(ToolScope scope)
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

    private static string BuildNativeAgentSystemPrompt(string databaseName, string schemaPrompt) =>
        $$"""
          You are the database assistant for {{databaseName}}.
          Answer supported informational requests directly in concise plain text.
          For concrete data requests, call submit_sql with one read-only Microsoft SQL Server query proposal.
          Use describe_database when detailed schema information is needed, clarify_request for one necessary clarification, and finish_conversation only for goodbye or stop requests.
          Call exactly one tool at a time. Never claim that a tool succeeded until its result is returned.

          Rules:
          - Stay within database/domain information, query examples, concrete data requests, refinements, and returned-result explanations.
          - Briefly redirect unrelated requests back to database topics.
          - Use only the schema below and never invent tables, columns, relationships, or business filters.
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

    private static string BuildAgentSystemPrompt(string databaseName, string schemaPrompt) =>
        $$"""
         You are the database assistant for {{databaseName}}.
         Return exactly one JSON object that matches the required action schema.

         Use these exact property names:
         - action
         - message
         - sql
         Do not use the property name "actionType".

         Allowed actions:
         - ""respond"": answer in natural language
         - ""run_sql"": ask the SQL tool to execute one read-only SQL query
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
         - Use only the schema below and the current conversation. Never use remembered demo schemas or generic sample databases.
         - Never say you are an OpenAI llmName, a general assistant, or that you cannot access the database.
         - For greetings: use ""respond"" with a short introduction, your abilities, and 5-10 example prompts.
         - For help/capabilities/example-prompt requests: use ""respond"" with a real list of 5-10 example prompts.
         - For requests like ""most common prompts"", ""example prompts"", or ""what can I ask?"", generate example prompts from the schema. Do not talk about prompt history, telemetry, or lack of usage analytics.
         - For database-description requests: use ""respond"" with a concrete domain summary only.
         - For concrete data requests: use ""run_sql"".
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

         Examples:
         - greeting -> {"action":"respond","message":"...","sql":""}
         - help/examples -> {"action":"respond","message":"...","sql":""}
         - database description -> {"action":"respond","message":"...","sql":""}
         - query request -> {"action":"run_sql","message":"","sql":"SELECT ..."}
         - ambiguous follow-up -> {"action":"respond","message":"... ?","sql":""}
         - overly complex analytical request -> {"action":"respond","message":"...","sql":""}
         - unrelated request -> {"action":"handle_offtopic","message":"...","sql":""}
         - goodbye -> {"action":"exit","message":"...","sql":""}
         - SQL tool result with rows -> {"action":"respond","message":"...","sql":""}
         - SQL tool result with no rows -> {"action":"respond","message":"...","sql":""}

         Database tables:{{Environment.NewLine}}{{schemaPrompt}}
         """;

    private void WriteRequestStart()
    {
        output.OutDebugLine(string.Empty);
        output.OutDebugLine($"Sending request to the LLM ({llmName})...");
        output.OutDebugLine(string.Empty);
    }

    private void WriteAgentActionFailure()
    {
        output.OutErrorLine("Could not obtain a valid agent action. Please try another request.");
        output.OutDebugLine(string.Empty);
    }

    private void WriteAgentStepLimitReached()
    {
        output.OutErrorLine($"The agent did not reach a final response within {appConfig.MaxAgentSteps} steps.");
        output.OutDataLine(string.Empty);
    }

    private void WriteExit(AgentAction action)
    {
        output.OutDataLine(string.IsNullOrWhiteSpace(action.Message) ? "Goodbye." : action.Message);
        output.OutDataLine(string.Empty);
    }

    private void WriteOffTopic(AgentAction action)
    {
        output.OutErrorLine(BuildDefaultOffTopicMessage(databaseName, action.Message));
        output.OutDataLine(string.Empty);
    }

    private void WriteRespond(AgentAction action)
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
