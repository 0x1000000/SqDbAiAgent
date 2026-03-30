using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Data.SqlClient;
using SqDbAiAgent.ConsoleApp.Conversation;
using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;
using SqExpress.DataAccess;
using SqExpress.SqlExport;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class DbChatSession(
    IConsoleOutput output,
    AppConfig appConfig,
    ISecurityFilter securityFilter,
    ILlmClient ollamaClient,
    ITablePrinter tablePrinter,
    IAgentTableFormatter agentTableFormatter,
    IMessageAnalyzeSession messageAnalyzeSession,
    ISqlApprovalSession sqlApprovalSession,
    int? userId,
    string schemaPrompt,
    string llmName,
    string connectionString,
    string databaseName,
    Func<string, ISqDatabase> dbFactory)
{
    private readonly string _agentSystemPrompt = BuildAgentSystemPrompt(databaseName, schemaPrompt);

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

            var approvalResult = await sqlApprovalSession.ApproveAsync(
                userRequest,
                action.Value.Sql,
                cancellationToken: cancellationToken
            );
            if (!approvalResult.Success)
            {
                this.WriteApprovalFailure(approvalResult);
                return true;
            }

            var executionResult = await this.TryExecuteApprovedQueryWithRuntimeRetryAsync(userRequest, approvalResult);
            if (executionResult is null)
            {
                output.OutDataLine(string.Empty);
                return true;
            }

            tablePrinter.Print(executionResult.Result);

            currentAgentInput = this.BuildToolResultMessage(
                userRequest,
                executionResult.ApprovedSql,
                executionResult.RenderedTable
            );
        }

        this.WriteAgentStepLimitReached();

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

    private bool TryGetExecutableReadOnlyQuery(
        SqlApprovalResult approvalResult,
        [NotNullWhen(true)] out IExprQuery? query)
    {
        if (approvalResult.ParsedExpression is not IExprQuery exprQuery)
        {
            output.OutErrorLine("Only queries are supported right now.");
            query = null;
            return false;
        }

        if (exprQuery is not IExprReadOnlyQuery)
        {
            output.OutErrorLine("You have only read access to the database.");
            query = null;
            return false;
        }

        query = exprQuery;
        return true;
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

    private async Task<QueryExecutionResult?> TryExecuteApprovedQueryWithRuntimeRetryAsync(
        string userRequest,
        SqlApprovalResult approvalResult)
    {
        var currentApproval = approvalResult;

        for (var attempt = 1; attempt <= appConfig.MaxSqlRuntimeFixAttempts + 1; attempt++)
        {
            if (!this.TryGetExecutableReadOnlyQuery(currentApproval, out var query))
            {
                return null;
            }

            if (!securityFilter.ValidateQuery(query, userId, out var safeQuery, out var error))
            {
                output.OutErrorLine(error);
                return null;
            }

            query = (IExprQuery)safeQuery;

            this.WriteAcceptedQuery(currentApproval.ApprovedSql, query);

            try
            {
                var result = await this.ExecuteQueryAsync(query);
                return new QueryExecutionResult(
                    currentApproval.ApprovedSql,
                    result,
                    agentTableFormatter.RenderMarkdown(result, appConfig.MaxAgentVisibleCells)
                );
            }
            catch (Exception ex)
            {
                var sqlException = FindSqlException(ex);
                if (sqlException is null)
                {
                    throw;
                }

                this.WriteRuntimeFailure(attempt, currentApproval.ApprovedSql, sqlException.Message);

                if (attempt > appConfig.MaxSqlRuntimeFixAttempts)
                {
                    this.WriteExecutionFailure("Could not execute query :( " + sqlException.Message);
                    return null;
                }

                currentApproval = await sqlApprovalSession.ApproveAsync(
                    userRequest,
                    currentApproval.ApprovedSql,
                    sqlException.Message,
                    "runtime"
                );

                if (!currentApproval.Success)
                {
                    this.WriteExecutionFailure(currentApproval.FailureMessage);
                    return null;
                }
            }
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
            return await ollamaClient.ChatAsync(
                llmName,
                messages,
                AgentAction.JsonSchema,
                thinkLevel: this.GetRetryThinkLevel(attempt),
                cancellationToken
            );
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

    private async Task<DataTable> ExecuteQueryAsync(IExprQuery expr)
    {
        await using var database = dbFactory(connectionString);

        return await database.Query(
            expr,
            new DataTable(),
            (table, reader) =>
            {
                if (table.Columns.Count == 0)
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        table.Columns.Add(GetUniqueColumnName(table, reader.GetName(i)));
                    }
                }

                var row = table.NewRow();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.GetValue(i);
                }

                table.Rows.Add(row);
                return table;
            }
        );
    }

    private static string GetUniqueColumnName(DataTable table, string columnName)
    {
        var baseName = string.IsNullOrWhiteSpace(columnName) ? "Column" : columnName;
        if (!table.Columns.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (table.Columns.Contains($"{baseName}_{suffix}"))
        {
            suffix++;
        }

        return $"{baseName}_{suffix}";
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

    private static SqlException? FindSqlException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
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

    private void WriteApprovalFailure(SqlApprovalResult approvalResult)
    {
        output.OutErrorLine(approvalResult.FailureMessage);
        output.OutDataLine(string.Empty);
    }

    private void WriteExecutionFailure(string message)
    {
        output.OutErrorLine(message);
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

    private void WriteAcceptedQuery(string sql, IExprQuery query)
    {
        output.OutDebugLine("Accepted query: ");
        output.OutDebug(sql);
        output.OutDebugLine(string.Empty);
        output.OutDebugLine("Query to execute: ");
        output.OutDebugLine(query.ToSql(TSqlExporter.Default));
        output.OutDebugLine(string.Empty);
    }

    private void WriteRuntimeFailure(int attempt, string sql, string errorMessage)
    {
        output.OutDebugLine($"SQL runtime failed on attempt {attempt}: {errorMessage}");
        output.OutDebugLine("Wrong T-SQL:");
        output.OutDebugLine(sql);
        output.OutDebugLine(string.Empty);
    }

    private sealed record QueryExecutionResult(string ApprovedSql, DataTable Result, RenderedTable RenderedTable);
}
