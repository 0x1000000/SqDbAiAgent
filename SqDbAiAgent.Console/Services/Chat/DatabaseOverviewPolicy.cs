using System.Text.RegularExpressions;

namespace SqDbAiAgent.ConsoleApp.Services.Chat;

public static partial class DatabaseOverviewPolicy
{
    public static bool IsOverviewRequest(string request)
    {
        var normalized = request.Trim();
        return DatabaseReferenceRegex().IsMatch(normalized)
               && OverviewIntentRegex().IsMatch(normalized);
    }

    public static bool IsUnhelpfulResponse(string response) =>
        UnhelpfulResponseRegex().IsMatch(response);

    public static string BuildFallback(string databaseName, string schemaPrompt)
    {
        var tables = TableNameRegex()
            .Matches(schemaPrompt)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        var entitySummary = tables.Length == 0
            ? "the entities and relationships listed in its configured schema"
            : string.Join(", ", tables);

        return
            $"Based on the exposed schema, the connected database stores data centered on {entitySummary}. "
            + "Their relationships indicate that the database supports managing and reporting on those domain workflows.";
    }

    [GeneratedRegex(@"\b(?:database|db|schema)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseReferenceRegex();

    [GeneratedRegex(@"\b(?:about|purpose|contain|contains|represent|used\s+for|what\s+is|describe|overview)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverviewIntentRegex();

    [GeneratedRegex(@"\b(?:cannot\s+answer|do\s+not\s+have\s+access|don't\s+have\s+access|ready\s+to\s+assist|provide\s+your\s+request)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnhelpfulResponseRegex();

    [GeneratedRegex(@"\[(?:dbo|[^\]]+)\]\.\[(?<name>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();
}
