using System.Text.RegularExpressions;

namespace SqDbAiAgent.ConsoleApp.Services;

public static partial class NativeAgentResponsePolicy
{
    public static bool RejectSqlAssistantText(string content)
    {
        return SqlMarkdownFenceRegex().IsMatch(content)
               || SqlStatementLineRegex().IsMatch(content);
    }

    [GeneratedRegex(@"```\s*(?:sql|t-?sql)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlMarkdownFenceRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*(?:SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlStatementLineRegex();
}
