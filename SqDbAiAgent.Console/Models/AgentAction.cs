using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public readonly record struct AgentAction(AgentActionType ActionType, string Message, string Sql)
{
    public static readonly JsonElement JsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["respond", "run_sql", "handle_offtopic", "exit"]
            },
            "message": {
              "type": "string"
            },
            "sql": {
              "type": "string"
            }
          },
          "required": ["action", "message", "sql"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public string ToJsonString()
    {
        return JsonSerializer.Serialize(new
        {
            action = ToWireValue(this.ActionType),
            message = this.Message,
            sql = this.Sql
        });
    }

    public static bool TryParseFromJson(string json, out AgentAction action)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                action = default;
                return false;
            }

            if ((!TryGetPropertyIgnoreCase(document.RootElement, "action", out var actionTypeElement)
                 && !TryGetPropertyIgnoreCase(document.RootElement, "actionType", out actionTypeElement))
                || actionTypeElement.ValueKind != JsonValueKind.String
                || !TryParseWireValue(actionTypeElement.GetString(), out var actionType))
            {
                action = default;
                return false;
            }

            var message = TryGetPropertyIgnoreCase(document.RootElement, "message", out var messageElement)
                          && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            var sql = TryGetPropertyIgnoreCase(document.RootElement, "sql", out var sqlElement)
                      && sqlElement.ValueKind == JsonValueKind.String
                ? sqlElement.GetString() ?? string.Empty
                : string.Empty;

            var isValid = actionType switch
            {
                AgentActionType.Respond => !string.IsNullOrWhiteSpace(message),
                AgentActionType.RunSql => !string.IsNullOrWhiteSpace(sql),
                AgentActionType.HandleOffTopic => true,
                AgentActionType.Exit => true,
                _ => false
            };

            if (!isValid)
            {
                action = default;
                return false;
            }

            action = new AgentAction(actionType, message, sql);
            return true;
        }
        catch (JsonException)
        {
            action = default;
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

    private static bool TryParseWireValue(string? value, out AgentActionType actionType)
    {
        switch (value)
        {
            case "respond":
                actionType = AgentActionType.Respond;
                return true;
            case "run_sql":
                actionType = AgentActionType.RunSql;
                return true;
            case "handle_offtopic":
                actionType = AgentActionType.HandleOffTopic;
                return true;
            case "exit":
                actionType = AgentActionType.Exit;
                return true;
            default:
                actionType = default;
                return false;
        }
    }

    private static string ToWireValue(AgentActionType actionType)
    {
        return actionType switch
        {
            AgentActionType.Respond => "respond",
            AgentActionType.RunSql => "run_sql",
            AgentActionType.HandleOffTopic => "handle_offtopic",
            AgentActionType.Exit => "exit",
            _ => throw new ArgumentOutOfRangeException(nameof(actionType), actionType, null)
        };
    }
}
