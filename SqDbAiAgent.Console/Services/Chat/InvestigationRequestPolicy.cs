using System.Text.RegularExpressions;

namespace SqDbAiAgent.ConsoleApp.Services.Chat;

public static partial class InvestigationRequestPolicy
{
    public static bool IsExplicitlyRequested(string userRequest)
    {
        return InvestigationTermRegex().IsMatch(userRequest);
    }

    [GeneratedRegex(
        @"\binvestigat(?:e|es|ed|ing|ion|ions|ive)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvestigationTermRegex();
}
