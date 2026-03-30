using System.ComponentModel.DataAnnotations;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed class AppConfig
{
    public const string SectionName = "App";

    [Required]
    public string ConnectionString { get; init; } =
        "Server=(local);Database=HarborFlow;Integrated Security=True;TrustServerCertificate=True";

    [Required]
    public string LlmProvider { get; init; } = "Ollama";

    public string LlmLogFilePath { get; init; } = @"C:\Temp\ollama.log";

    // Maximum number of agent decision steps for a single user request before the loop is stopped.
    [Range(1, 100)]
    public int MaxAgentSteps { get; init; } = 5;

    // Maximum number of visible result cells sent back to the agent in Markdown form.
    [Range(1, 100000)]
    public int MaxAgentVisibleCells { get; init; } = 1000;

    // Maximum number of parser-driven SQL repair attempts before giving up.
    [Range(1, 100)]
    public int MaxSqlFixAttempts { get; init; } = 10;

    // Maximum number of execution-time repair attempts after SQL Server rejects a parsed query.
    [Range(1, 100)]
    public int MaxSqlRuntimeFixAttempts { get; init; } = 3;

    // Maximum number of attempts to get a valid classified JSON response from the model.
    [Range(1, 100)]
    public int MaxClassificationAttempts { get; init; } = 3;

    // Retry attempt number at which low thinking is enabled for repeated LLM calls.
    [Range(1, 100)]
    public int ThinkAfterAttempt { get; init; } = 3;

    // Overall reasoning mode for model calls: Auto uses retry-based escalation, Enabled always enables reasoning, Disabled never enables it.
    public LlmReasoningMode Reasoning { get; init; } = LlmReasoningMode.Auto;

    // Maximum number of attempts to get a valid SQL-fix JSON response for one repair step.
    [Range(1, 100)]
    public int MaxFixResponseAttempts { get; init; } = 3;

    // Maximum number of times the model may repeat the same SQL before the fix step is aborted.
    [Range(1, 100)]
    public int MaxUnchangedSqlResponses { get; init; } = 2;

    // Total character budget for one LLM prompt, including system prompt, history, and current request.
    [Range(1000, 1000000)]
    public int MaxPromptChars { get; init; } = 32000;

    // Reserved characters to avoid overrunning the prompt budget with formatting overhead.
    [Range(0, 1000000)]
    public int PromptSafetyChars { get; init; } = 1500;
}
