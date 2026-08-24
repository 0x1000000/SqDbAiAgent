using System.ComponentModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SqDbAiAgent.ConsoleApp.Models;
using AppChatMessage = SqDbAiAgent.ConsoleApp.Models.ChatMessage;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class AgentFrameworkDbChatSession : IDbChatSession
{
    private readonly IConsoleOutput _output;
    private readonly AppConfig _appConfig;
    private readonly IMessageAnalyzeSession _messageAnalyzeSession;
    private readonly ValidatedSqlExecutor _sqlExecutor;
    private readonly string _schemaPrompt;
    private readonly AIAgent _agent;
    private readonly List<AppChatMessage> _analysisHistory = [];
    private AgentSession? _agentSession;
    private string? _clarificationQuestion;
    private string? _finishMessage;
    private string _currentUserRequest = string.Empty;

    public AgentFrameworkDbChatSession(
        IConsoleOutput output,
        AppConfig appConfig,
        IChatClient chatClient,
        IMessageAnalyzeSession messageAnalyzeSession,
        ValidatedSqlExecutor sqlExecutor,
        string databaseName,
        string schemaPrompt)
    {
        this._output = output;
        this._appConfig = appConfig;
        this._messageAnalyzeSession = messageAnalyzeSession;
        this._sqlExecutor = sqlExecutor;
        this._schemaPrompt = schemaPrompt;

        var tools = BuildTools(appConfig.ToolScope);
        var functionInvokingClient = new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = appConfig.MaxAgentSteps
        };
        this._agent = functionInvokingClient.AsAIAgent(
            instructions: BuildSystemPrompt(databaseName, schemaPrompt),
            name: "SqDbAiAgent",
            tools: tools);
    }

    public async Task<bool> HandleInputAsync(string userRequest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return true;
        }

        this._output.OutDebugLine("Sending request to the LLM (Microsoft Agent Framework)...");
        this._output.OutDebugLine(string.Empty);

        var analysis = await this.AnalyzeMessageAsync(userRequest.Trim(), cancellationToken);
        if (analysis is null)
        {
            this._output.OutErrorLine("Could not analyze the request.");
            return true;
        }

        if (this._agentSession is null || analysis.Value.IsNewTopic)
        {
            this._agentSession = await this._agent.CreateSessionAsync(cancellationToken);
        }

        this._clarificationQuestion = null;
        this._finishMessage = null;
        this._currentUserRequest = userRequest.Trim();
        var instruction = userRequest.Trim();

        for (var attempt = 1; attempt <= this._appConfig.MaxAgentSteps; attempt++)
        {
            AgentResponse response;
            try
            {
                response = await this._agent.RunAsync(instruction, this._agentSession, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                this._output.OutDebugLine($"Agent Framework call failed on attempt {attempt}: {ex.Message}");
                this._output.OutDebugLine(string.Empty);
                if (!LlmRetryPolicy.ShouldRetry(ex))
                {
                    throw;
                }

                continue;
            }

            if (this._finishMessage is not null)
            {
                WriteAssistant(this._finishMessage);
                this.AppendAnalysisTurn(userRequest, this._finishMessage);
                return false;
            }

            if (this._clarificationQuestion is not null)
            {
                WriteAssistant(this._clarificationQuestion);
                this.AppendAnalysisTurn(userRequest, this._clarificationQuestion);
                return true;
            }

            var text = response.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                instruction = "Return a concise plain-text answer or call one available tool.";
                continue;
            }

            if (NativeAgentResponsePolicy.RejectSqlAssistantText(text))
            {
                this._output.OutDebugLine("Agent Framework returned SQL as assistant text instead of calling submit_sql.");
                this._output.OutDebugLine(string.Empty);
                instruction = "Your previous response was invalid because it returned SQL as assistant text. Call submit_sql with that SQL now. Do not return SQL or an answer from memory.";
                continue;
            }

            WriteAssistant(text);
            this.AppendAnalysisTurn(userRequest, text);
            return true;
        }

        this._output.OutErrorLine("The Agent Framework step limit was reached before a valid answer was produced.");
        return true;
    }

    private IList<AITool> BuildTools(ToolScope scope)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                this.SubmitSqlAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "submit_sql",
                    Description = "Submit one self-contained read-only SQL Server query for validation, security filtering, and execution."
                })
        };

        if (scope == ToolScope.Full)
        {
            tools.Add(AIFunctionFactory.Create(
                this.DescribeDatabase,
                new AIFunctionFactoryOptions
                {
                    Name = "describe_database",
                    Description = "Return the detailed permitted database schema and relationships."
                }));
            tools.Add(AIFunctionFactory.Create(
                this.ClarifyRequest,
                new AIFunctionFactoryOptions
                {
                    Name = "clarify_request",
                    Description = "Ask one necessary clarification question and wait for the user's next message."
                }));
            tools.Add(AIFunctionFactory.Create(
                this.FinishConversation,
                new AIFunctionFactoryOptions
                {
                    Name = "finish_conversation",
                    Description = "End the conversation, optionally displaying a short closing message."
                }));
        }

        return tools;
    }

    [Description("Submit SQL through the application's validation and security pipeline.")]
    private async Task<string> SubmitSqlAsync(
        [Description("One self-contained read-only Microsoft SQL Server query.")] string sql,
        CancellationToken cancellationToken)
    {
        var result = await this._sqlExecutor.SubmitAsync(this._currentUserRequest, sql, cancellationToken);
        return result is null
            ? "SQL was rejected or could not be executed. Correct it and call submit_sql again."
            : BuildToolResult(result);
    }

    private string DescribeDatabase() => this._schemaPrompt;

    private string ClarifyRequest(
        [Description("The concise clarification question to show to the user.")] string question)
    {
        this._clarificationQuestion = question.Trim();
        return "The clarification question was displayed. Stop this turn and wait for the user.";
    }

    private string FinishConversation(
        [Description("Optional concise closing message.")] string? message = null)
    {
        if (!ConversationExitPolicy.IsExplicitExitRequest(this._currentUserRequest))
        {
            return "Rejected: the user did not explicitly ask to end the conversation. Continue answering the current request.";
        }

        this._finishMessage = message?.Trim() ?? string.Empty;
        return "The conversation was ended.";
    }

    private async Task<MessageAnalysisResult?> AnalyzeMessageAsync(string userRequest, CancellationToken cancellationToken)
    {
        var classification = await this._messageAnalyzeSession.ClassifyAsync(
            this._analysisHistory,
            userRequest,
            cancellationToken);
        if (classification is null)
        {
            return null;
        }

        var topic = await this._messageAnalyzeSession.CheckNewTopicAsync(
            this._analysisHistory,
            userRequest,
            cancellationToken);
        if (topic is null)
        {
            return null;
        }

        var result = new MessageAnalysisResult(
            classification.Value.Kind,
            classification.Value.Kind == MessageKind.FollowUp ? false : topic.Value.IsNewTopic);
        this._output.OutDebugLine(
            $"Message analysis: kind={result.Kind}, isNewTopic={result.IsNewTopic.ToString().ToLowerInvariant()}");
        this._output.OutDebugLine(string.Empty);
        return result;
    }

    private void AppendAnalysisTurn(string userRequest, string assistantText)
    {
        this._analysisHistory.Add(new AppChatMessage("user", userRequest));
        this._analysisHistory.Add(new AppChatMessage("assistant", assistantText));

        var maxChars = Math.Max(1000, this._appConfig.MaxPromptChars - this._appConfig.PromptSafetyChars);
        while (this._analysisHistory.Sum(message => message.Content.Length) > maxChars && this._analysisHistory.Count >= 2)
        {
            this._analysisHistory.RemoveRange(0, 2);
        }
    }

    private static string BuildToolResult(ValidatedSqlExecutionResult result)
    {
        var table = result.RenderedTable;
        var builder = new StringBuilder();
        builder.AppendLine("The validated SQL query completed successfully.");
        builder.AppendLine($"Approved SQL: {result.ApprovedSql}");
        builder.AppendLine($"Result rows: {table.TotalRows}");
        builder.AppendLine($"Visible result shape: {table.ShownRows} row(s), {table.ShownColumns} column(s), {table.ShownCells} cell(s).");
        if (table.Truncated)
        {
            builder.AppendLine("The visible result was truncated by the configured cell budget.");
        }

        builder.AppendLine("Visible result grid:");
        builder.AppendLine(table.Markdown);
        builder.AppendLine("Summarize briefly in plain text. Do not render or reconstruct a table.");
        return builder.ToString().TrimEnd();
    }

    private void WriteAssistant(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            this._output.OutDataLine(text);
            this._output.OutDataLine(string.Empty);
        }
    }

    private static string BuildSystemPrompt(string databaseName, string schemaPrompt) =>
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
          - SQL intended to answer a data request must appear only in the sql argument of submit_sql.
          - Never use LIMIT, RETURNING, markdown fences, comments, placeholders, variables, or parameters in SQL.
          - Prefer the smallest query that answers the request and avoid unnecessarily broad result sets.
          - Never render or reconstruct a table in assistant text. The application renders tables; summarize visible results only.

          Initial allowed database schema:
          {{schemaPrompt}}
          """;
}
