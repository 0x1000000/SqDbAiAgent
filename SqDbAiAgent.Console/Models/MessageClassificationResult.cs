using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public readonly record struct MessageClassificationResult(MessageKind Kind)
{
    public static readonly JsonElement JsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["general_information", "off_topic", "request", "follow_up"]
            }
          },
          "required": ["kind"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public string ToJsonString()
    {
        return JsonSerializer.Serialize(new
        {
            kind = ToWireValue(this.Kind)
        });
    }

    public static bool TryParseFromJson(string json, out MessageClassificationResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                result = default;
                return false;
            }

            if (!TryGetPropertyIgnoreCase(document.RootElement, "kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !TryParseWireValue(kindElement.GetString(), out var kind))
            {
                result = default;
                return false;
            }

            result = new MessageClassificationResult(kind);
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

    private static bool TryParseWireValue(string? value, out MessageKind kind)
    {
        switch (value)
        {
            case "general_information":
                kind = MessageKind.GeneralInformation;
                return true;
            case "off_topic":
                kind = MessageKind.OffTopic;
                return true;
            case "request":
                kind = MessageKind.Request;
                return true;
            case "follow_up":
                kind = MessageKind.FollowUp;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string ToWireValue(MessageKind kind)
    {
        return kind switch
        {
            MessageKind.GeneralInformation => "general_information",
            MessageKind.OffTopic => "off_topic",
            MessageKind.Request => "request",
            MessageKind.FollowUp => "follow_up",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
