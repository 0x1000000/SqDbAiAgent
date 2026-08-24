namespace SqDbAiAgent.ConsoleApp.Services;

public static class ConversationExitPolicy
{
    private static readonly HashSet<string> ExitMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "bye",
        "goodbye",
        "exit",
        "quit",
        "stop",
        "end conversation",
        "finish conversation",
        "that's all",
        "thats all",
        "no more questions"
    };

    public static bool IsExplicitExitRequest(string message)
    {
        var normalized = message.Trim().TrimEnd('.', '!', '?').Trim();
        if (ExitMessages.Contains(normalized))
        {
            return true;
        }

        var finalPhrase = normalized
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return finalPhrase is not null && ExitMessages.Contains(finalPhrase);
    }
}
