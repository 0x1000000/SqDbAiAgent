namespace SqDbAiAgent.ConsoleApp.Models.Llm;

public sealed record LlmChatResult(string Content, IReadOnlyList<LlmToolCall> ToolCalls)
{
    public static LlmChatResult FromContent(string content) => new(content, []);
}
