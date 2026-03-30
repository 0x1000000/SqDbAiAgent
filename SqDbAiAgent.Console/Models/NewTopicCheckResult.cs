using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public readonly record struct NewTopicCheckResult(bool IsNewTopic)
{
    public static readonly JsonElement JsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "isNewTopic": {
              "type": "boolean"
            }
          },
          "required": ["isNewTopic"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public string ToJsonString()
    {
        return JsonSerializer.Serialize(new
        {
            isNewTopic = this.IsNewTopic
        });
    }

    public static bool TryParseFromJson(string json, out NewTopicCheckResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                result = default;
                return false;
            }

            if (!TryGetPropertyIgnoreCase(document.RootElement, "isNewTopic", out var valueElement)
                || (valueElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
            {
                result = default;
                return false;
            }

            result = new NewTopicCheckResult(valueElement.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            result = default;
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
