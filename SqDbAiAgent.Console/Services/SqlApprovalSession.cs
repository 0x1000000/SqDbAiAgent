using System.Text.RegularExpressions;
using SqDbAiAgent.ConsoleApp.Conversation;
using SqDbAiAgent.ConsoleApp.Helpers;
using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;
using SqExpress.SqlParser;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class SqlApprovalSession : ISqlApprovalSession
{
    private readonly AppConfig _appConfig;
    private readonly ILlmClient _llmClient;
    private readonly IConsoleOutput _output;
    private readonly string _llmName;
    private readonly IReadOnlyList<TableBase> _publicTables;
    private readonly string _systemPrompt;
    private readonly ChatHistoryManager<LlmResponse> _conversationHistory;

    public SqlApprovalSession(
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
        this._publicTables = publicTables;
        this._systemPrompt = BuildSqlFixSystemPrompt(databaseName, schemaPrompt);
        this._conversationHistory = new ChatHistoryManager<LlmResponse>(
            this._appConfig.MaxPromptChars,
            response => response.ToJsonString(),
            response => response.Text.Length);
    }

    public async Task<SqlApprovalResult> ApproveAsync(
        string userRequest,
        string proposedSql,
        string? error = null,
        string errorKind = "parser",
        CancellationToken cancellationToken = default)
    {
        var currentSql = NormalizeSqlText(proposedSql);
        var attempt = 0;

        while (attempt < this._appConfig.MaxSqlFixAttempts)
        {
            attempt++;
            var hasExternalError = !string.IsNullOrWhiteSpace(error);
            var approvalError = string.Empty;

            if (!hasExternalError
                && this.TryApproveSql(currentSql, out var parsedExpression, out approvalError))
            {
                var removedCount = this._conversationHistory.Push(
                    userRequest,
                    new LlmResponse(LlmResponseType.TSqlCode, currentSql));
                if (removedCount > 0)
                {
                    this._output.OutDebugLine($"SQL fixer history trimmed. Removed {removedCount} old turn(s).");
                }

                return SqlApprovalResult.Approved(currentSql, parsedExpression!);
            }

            var effectiveError = hasExternalError
                ? error!
                : approvalError;

            this._output.OutDebugLine($"SQL approval failed on attempt {attempt}: {effectiveError}");
            this._output.OutDebugLine("Wrong T-SQL:");
            this._output.OutDebugLine(currentSql);
            this._output.OutDebugLine(string.Empty);

            if (attempt == this._appConfig.MaxSqlFixAttempts)
            {
                return SqlApprovalResult.Failed(
                    $"Could not produce valid query after {this._appConfig.MaxSqlFixAttempts} attempts. Please rephrase your request.");
            }

            var fixedResponse = await this.TryGetSqlFixResponseAsync(
                userRequest,
                currentSql,
                effectiveError,
                errorKind,
                cancellationToken);

            if (fixedResponse is null)
            {
                return SqlApprovalResult.Failed(
                    $"Could not produce valid query after {this._appConfig.MaxSqlFixAttempts} attempts. Please rephrase your request.");
            }

            currentSql = NormalizeSqlText(fixedResponse.Value.Text);
            error = null;
            errorKind = "parser";
        }

        return SqlApprovalResult.Failed(
            $"Could not produce valid query after {this._appConfig.MaxSqlFixAttempts} attempts. Please rephrase your request.");
    }

    private async Task<LlmResponse?> TryGetSqlFixResponseAsync(
        string userRequest,
        string currentSql,
        string error,
        string errorKind,
        CancellationToken cancellationToken)
    {
        var currentInstruction = BuildSqlFixInstruction(
            userRequest,
            currentSql,
            error,
            errorKind,
            strictRetry: false,
            repeatedSameSql: false);

        var unchangedSqlResponses = 0;

        for (var attempt = 1; attempt <= this._appConfig.MaxFixResponseAttempts; attempt++)
        {
            var messages = this.BuildMessages(currentInstruction);
            var reply = await this.TryChatJsonAsync(messages, $"{errorKind} SQL fix", attempt, cancellationToken);
            if (reply is null)
            {
                currentInstruction = BuildSqlFixInstruction(userRequest, currentSql, error, errorKind, strictRetry: true, repeatedSameSql: false);
                continue;
            }

            var response = TryParseResponse(reply);
            if (response is { } fixResponse && fixResponse.RespType == LlmResponseType.TSqlCode)
            {
                if (!IsSameSql(currentSql, fixResponse.Text))
                {
                    return fixResponse;
                }

                unchangedSqlResponses++;
                this._output.OutDebugLine($"SQL fix response repeated the same SQL on attempt {attempt}.");
                this._output.OutDebugLine("Wrong T-SQL returned again:");
                this._output.OutDebugLine(fixResponse.Text);
                this._output.OutDebugLine(string.Empty);

                if (unchangedSqlResponses >= this._appConfig.MaxUnchangedSqlResponses)
                {
                    break;
                }

                currentInstruction = BuildSqlFixInstruction(userRequest, currentSql, error, errorKind, strictRetry: true, repeatedSameSql: true);
                continue;
            }

            this._output.OutDebugLine($"Could not understand the SQL fix response as valid T-SQL JSON on attempt {attempt}.");
            this._output.OutDebugLine("Raw model response:");
            this._output.OutDebugLine(reply);
            this._output.OutDebugLine(string.Empty);

            currentInstruction = BuildSqlFixInstruction(userRequest, currentSql, error, errorKind, strictRetry: true, repeatedSameSql: false);
        }

        return null;
    }

    private bool TryApproveSql(
        string sql,
        out SqExpress.Syntax.IExpr? parsedExpression,
        out string error)
    {
        parsedExpression = null;
        error = string.Empty;

        if (ContainsSqlParameterPlaceholder(sql))
        {
            error = "Pure SQL parameters are not supported. Return a self-contained query without placeholders like @id or @branchId.";
            return false;
        }

        if (!SqTSqlParser.TryParse(sql, out var expr, out var parsedTables, out var parseError))
        {
            error = string.IsNullOrWhiteSpace(parseError)
                ? "Unknown SQL parse error."
                : parseError;

            if (string.IsNullOrWhiteSpace(error))
            {
                error = "Unknown SQL parse error.";
            }

            return false;
        }

        var comparison = parsedTables.CompareWith(this._publicTables, SqExpressHelpers.BuildTableComparisonKey);
        if (comparison is not null)
        {
            error = BuildParsedTableMismatchError(comparison, this._publicTables) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }
        }

        parsedExpression = expr;
        return true;
    }

    private async Task<string?> TryChatJsonAsync(
        IReadOnlyList<ChatMessage> messages,
        string operationName,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await this._llmClient.ChatAsync(
                this._llmName,
                messages,
                LlmResponse.JsonSchema,
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

    private List<ChatMessage> BuildMessages(string currentInstruction)
    {
        var messages = new List<ChatMessage>
        {
            new("system", this._systemPrompt)
        };

        var remainingBudget = this._appConfig.MaxPromptChars
                             - this._systemPrompt.Length
                             - currentInstruction.Length
                             - this._appConfig.PromptSafetyChars;

        if (remainingBudget > 0)
        {
            messages.AddRange(this._conversationHistory.BuildHistory(remainingBudget));
        }

        messages.Add(new ChatMessage("user", currentInstruction));
        return messages;
    }

    private static string BuildSqlFixSystemPrompt(string databaseName, string schemaPrompt)
    {
        return
            $@"
You repair and approve Microsoft SQL Server queries for {databaseName}.
Return exactly one JSON object matching the SQL-response schema.

 Rules:
 - Use these exact property names in the JSON response:
   - respType
   - text
 - Do not use property names like sql, message, or response.
 - Only return respType = ""t-sql code"".
 - Return only corrected Microsoft SQL Server T-SQL in text.
 - For straightforward aggregate or ranking requests, prefer a simple SELECT with JOIN, WHERE, GROUP BY, and ORDER BY instead of CTEs, window functions, or helper subqueries.
 - Use only the schema below.
 - Never use remembered sample schemas or objects not listed below.
 - No markdown, comments, explanations, placeholders, variables, or parameters.
 - No LIMIT, RETURNING, PostgreSQL/MySQL syntax, or non-T-SQL features.
 - Use exact schema-qualified table names.
 - Use only listed columns.
 - Do not invent extra business filters such as completed, active, shipped, paid, or cancelled unless the user explicitly asks for them or the schema clearly requires them.
 - When joining related tables, prefer the foreign-key relationship shown by the schema, not a guess based on similarly named primary keys.
 - For grouped time-period queries such as by month, week, quarter, or year, prefer one grouped query instead of many UNION ALL branches.
 - For month-name output, prefer DATENAME(MONTH, <date>) as the label and also include MONTH(<date>) or a similar numeric sort key so calendar order is preserved.
 - If a textual month label is needed and direct date-name functions are not parser-friendly, prefer a CASE expression on a numeric month bucket in a derived table.
 - For ""this year"", prefer a start-of-year boundary such as DATEFROMPARTS(YEAR(GETDATE()), 1, 1) and an exclusive upper bound one year later.
 - Prefer inline date boundaries in the WHERE clause instead of separate scalar CTEs such as YearStart or YearEnd.
 - Fix the SQL directly. Do not restate the problem.

Database tables:
"
            + Environment.NewLine
            + schemaPrompt;
    }

    private static string BuildSqlFixInstruction(
        string userRequest,
        string currentSql,
        string error,
        string errorKind,
        bool strictRetry,
        bool repeatedSameSql)
    {
        var errorLabel = string.Equals(errorKind, "runtime", StringComparison.OrdinalIgnoreCase)
            ? "Runtime error"
            : "Parser error";
        var retryLine = strictRetry
            ? $"Your previous response was invalid. Think harder about the {errorKind} error. You must return ONLY respType = \"t-sql code\" and ONLY corrected T-SQL JSON."
            : string.Empty;
        var repeatedSqlLine = repeatedSameSql
            ? """
              Your previous fix repeated the same broken SQL.
              Think harder and produce a materially different correction.
              You must change the SQL text.
              Do not repeat the same query again.
              Correct the parser or runtime error directly in the new SQL.
              """
            : string.Empty;
        var repairRules = BuildSqlRepairRules(error);

        return
            $$"""
              The previous SQL was invalid for the provided database schema and must be fixed.
              Return a response that matches the provided JSON schema exactly.
              {{retryLine}}
              {{repeatedSqlLine}}

              Original user request:
              {{userRequest}}

              Rejected SQL that must be corrected:
              {{currentSql}}

              {{errorLabel}}:
              {{repairRules}}

              Requirements:
              - Use these exact JSON property names: respType and text
              - Do not use JSON property names like sql, response, or message
              - Expected response type is ONLY "t-sql code"
              - Do not return "dbInfo"
              - Do not return "warning"
              - Stay on the original user request and correct only the SQL
              - Do not explain anything
              - Return only corrected Microsoft SQL Server T-SQL
              - Think harder about the error and correct it directly
              - Previous SQL was rejected. Returning it again is always wrong.
              - Use the rejected SQL above as the starting point and change only what is needed to fix the reported error.
              - Always use the exact schema-qualified table names from the provided schema
              - Do not assume dbo schema
              - If a rule says "Make sure that [schema].[Table] can have only columns ...", do not use any other columns from that table
              - If a rule says LIMIT is not supported, use TOP (n) in the SELECT clause instead
              - Do not return SQL parameters like @id, @branchId, or @name
              """;
    }

    private static string BuildParsedTableMismatchError(
        TableListComparison comparison,
        IReadOnlyList<TableBase> publicTables)
    {
        var unexpectedTables = comparison.MissedTables
            .Select(SqExpressHelpers.FormatTableName)
            .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tableDifferences = new List<string>();
        var allowedColumnsByTable = new List<string>();
        foreach (var tableDifference in comparison.DifferentTables.OrderBy(
                     i => SqExpressHelpers.BuildTableComparisonKey(i.Table.FullName),
                     StringComparer.Ordinal
                 ))
        {
            var tableDiff = SqExpressHelpers.BuildTableDifferenceMessage(tableDifference.Table, tableDifference.TableComparison);
            if (string.IsNullOrEmpty(tableDiff))
            {
                continue;
            }

            tableDifferences.Add(tableDiff);
            var matchingTable = SqExpressHelpers.FindMatchingTable(publicTables, tableDifference.Table) ?? tableDifference.Table;
            allowedColumnsByTable.Add(
                $"Allowed columns for {SqExpressHelpers.FormatTableName(matchingTable)}: {string.Join(", ", SqExpressHelpers.GetAvailableColumns(matchingTable).Select(c => $"[{c}]"))}"
            );
        }

        if (unexpectedTables.Count == 0 && tableDifferences.Count == 0)
        {
            return null!;
        }

        var parts = new List<string>
        {
            "Parsed SQL table artifacts do not match provided existing tables."
        };

        if (unexpectedTables.Count > 0)
        {
            parts.Add("Unexpected tables: " + string.Join(", ", unexpectedTables));
        }

        if (tableDifferences.Count > 0)
        {
            parts.Add("Table differences: " + string.Join("; ", tableDifferences));
        }

        parts.AddRange(allowedColumnsByTable.Distinct(StringComparer.Ordinal));
        return string.Join(Environment.NewLine, parts);
    }

    private static string BuildSqlRepairRules(string error)
    {
        var lines = error
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();

        foreach (var line in lines)
        {
            result.Add(line);

            if (line.StartsWith("Allowed columns for ", StringComparison.Ordinal))
            {
                result.Add("Make sure that " + line["Allowed columns for ".Length..]);
            }

            if (line.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("Make sure that LIMIT is not supported in T-SQL. Use TOP (n) in the SELECT clause.");
            }
        }

        if (error.Contains("Unqualified column reference is ambiguous", StringComparison.Ordinal))
        {
            result.Add("Make sure that all columns in multi-table queries are fully qualified with table aliases.");
            result.Add("Make sure that ORDER BY does not use an ambiguous alias. Prefer a fully qualified column or the aggregate expression.");
        }

        if (error.Contains("Unknown table alias or name", StringComparison.Ordinal))
        {
            result.Add("Make sure that every alias used in SELECT, WHERE, GROUP BY, and ORDER BY is defined in FROM or JOIN and is used consistently.");
        }

        if (error.Contains("Operand type clash", StringComparison.OrdinalIgnoreCase)
            && error.Contains("date", StringComparison.OrdinalIgnoreCase)
            && error.Contains("int", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Make sure that dates are adjusted with DATEADD(...), not by adding or subtracting integers from a DATE expression.");
            result.Add("Make sure that date filters use valid SQL Server date arithmetic.");
            result.Add("Make sure that 'this year' starts at the beginning of the current year, for example with DATEFROMPARTS(YEAR(GETDATE()), 1, 1).");
            result.Add("Make sure that year-based date filters are applied to the correct date column, not to an invented detail-row date column.");
        }

        if (error.Contains("unbalanced parentheses", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Make sure that derived tables are written as (SELECT ...) AS [Alias] and not as ([SELECT ...]) or other bracketed wrappers.");
            result.Add("Make sure that grouped time-period queries use a normal SELECT with GROUP BY instead of a generated UNION ALL block for each month or period.");
        }

        if (error.Contains("Select item is not supported", StringComparison.OrdinalIgnoreCase)
            && error.Contains("DATENAME", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Make sure that textual month labels use a CASE expression on a numeric month bucket instead of DATENAME(...) if the parser rejects direct date-name functions.");
            result.Add("Make sure that grouped month queries can be split into an inner query that computes MonthNum and an outer query that maps MonthNum to a text label.");
        }

        if (error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
            && error.Contains("MONTH", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Make sure that function calls and aliases use clear derived-table column names such as MonthNum to avoid ambiguity in grouped queries.");
        }

        if (error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("YearStart", StringComparison.OrdinalIgnoreCase)
                || error.Contains("YearEnd", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("Make sure that date boundaries are written directly in the WHERE clause instead of as scalar CTE names like YearStart or YearEnd.");
        }

        if (error.Contains("extra columns", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("Status", StringComparison.OrdinalIgnoreCase)
                || error.Contains("LineTotal", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("Make sure that you do not invent status or total columns that are not listed in the schema.");
            result.Add("Make sure that totals are computed from listed quantity and price columns when no stored total column is available.");
        }

        if (error.Contains("Pure SQL parameters are not supported", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Make sure that the query is self-contained and does not use placeholders like @id, @name, or @branchId.");
            result.Add("Make sure that no SQL variables or parameters are returned.");
        }

        var extraColumnMatches = Regex.Matches(
            error,
            @"(\[[^\]]+\]\.\[[^\]]+\]), extra columns: ([^\r\n;]+)",
            RegexOptions.CultureInvariant);
        foreach (Match match in extraColumnMatches)
        {
            if (!match.Success)
            {
                continue;
            }

            result.Add($"Make sure that {match.Groups[1].Value} does not use columns {match.Groups[2].Value}.");
            result.Add($"Make sure that {match.Groups[1].Value} uses only columns listed in its allowed-columns rule.");
        }

        return string.Join(Environment.NewLine, result.Distinct(StringComparer.Ordinal));
    }

    private static bool IsSameSql(string left, string right)
    {
        return string.Equals(NormalizeSql(NormalizeSqlText(left)), NormalizeSql(NormalizeSqlText(right)), StringComparison.Ordinal);
    }

    private static string NormalizeSql(string sql)
    {
        return Regex.Replace(sql, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string NormalizeSqlText(string sql)
    {
        var trimmed = sql.Trim();

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

    private static bool ContainsSqlParameterPlaceholder(string sql)
    {
        return Regex.IsMatch(
            sql,
            @"(?<!@)@[A-Za-z_][A-Za-z0-9_]*",
            RegexOptions.CultureInvariant);
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

    private static LlmResponse? TryParseResponse(string reply)
    {
        var trimmed = reply.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
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

            trimmed = trimmed.Trim();
        }

        return LlmResponse.TryParseFromJson(trimmed, out var response)
            ? response
            : null;
    }
}
