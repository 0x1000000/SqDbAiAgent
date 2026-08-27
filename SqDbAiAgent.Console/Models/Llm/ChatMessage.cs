namespace SqDbAiAgent.ConsoleApp.Models.Llm;

public sealed record ChatMessage(
    string Role,
    string Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);
