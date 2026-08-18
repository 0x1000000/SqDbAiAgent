namespace SqDbAiAgent.ConsoleApp.Models;

public sealed record LlmChatResult(string Content, IReadOnlyList<LlmToolCall> ToolCalls)
{
    public static LlmChatResult FromContent(string content) => new(content, []);
}
