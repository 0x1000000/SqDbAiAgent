using System.Text.RegularExpressions;

namespace SqDbAiAgent.ConsoleApp.Services.Chat;

public static partial class NativeAgentResponsePolicy
{
    public static bool RejectSqlAssistantText(string content)
    {
        return SqlMarkdownFenceRegex().IsMatch(content)
               || SqlStatementLineRegex().IsMatch(content);
    }

    public static bool RejectRenderedTableAssistantText(string content)
    {
        return MarkdownTableSeparatorRegex().IsMatch(content);
    }

    public static bool RejectPseudoToolCallAssistantText(string content)
    {
        return PseudoToolCallRegex().IsMatch(content);
    }

    public static bool RejectJsonAssistantText(string content)
    {
        var trimmed = content.Trim();
        return trimmed.StartsWith('{') && trimmed.EndsWith('}');
    }

    [GeneratedRegex(@"```\s*(?:sql|t-?sql)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlMarkdownFenceRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*(?:SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlStatementLineRegex();

    [GeneratedRegex(
        @"(?:^|\r?\n)\s*\|?(?:\s*:?-{3,}:?\s*\|){1,}\s*:?-{3,}:?\s*\|?\s*(?:\r?\n|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownTableSeparatorRegex();

    [GeneratedRegex(
        @"""(?:tool|name)""\s*:\s*""(?:execute_sql|submit_sql|investigate_sql|describe_database|clarify_request|finish_conversation)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PseudoToolCallRegex();
}
