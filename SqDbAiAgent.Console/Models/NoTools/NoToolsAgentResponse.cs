using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models.NoTools;

/// <summary>
/// Structured JSON response used when native LLM tool calling is disabled or when native orchestration falls back.
/// </summary>
public readonly record struct NoToolsAgentResponse(NoToolsAgentResponseType ActionType, string Message, string Sql, string Purpose = "")
{
    public static readonly JsonElement JsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["respond", "run_sql", "investigate_sql", "handle_offtopic", "exit"]
            },
            "message": {
              "type": "string"
            },
            "sql": {
              "type": "string"
            },
            "purpose": {
              "type": "string"
            }
          },
          "required": ["action", "message", "sql", "purpose"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public static readonly JsonElement JsonSchemaWithoutInvestigation = JsonDocument.Parse(
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
            },
            "purpose": {
              "type": "string"
            }
          },
          "required": ["action", "message", "sql", "purpose"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public string ToJsonString()
    {
        return JsonSerializer.Serialize(new
        {
            action = ToWireValue(this.ActionType),
            message = this.Message,
            sql = this.Sql,
            purpose = this.Purpose
        });
    }

    public static bool TryParseFromJson(string json, out NoToolsAgentResponse action)
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

            var purpose = TryGetPropertyIgnoreCase(document.RootElement, "purpose", out var purposeElement)
                          && purposeElement.ValueKind == JsonValueKind.String
                ? purposeElement.GetString() ?? string.Empty
                : string.Empty;

            var isValid = actionType switch
            {
                NoToolsAgentResponseType.Respond => !string.IsNullOrWhiteSpace(message),
                NoToolsAgentResponseType.RunSql => !string.IsNullOrWhiteSpace(sql),
                NoToolsAgentResponseType.InvestigateSql => !string.IsNullOrWhiteSpace(sql)
                                                  && !string.IsNullOrWhiteSpace(purpose),
                NoToolsAgentResponseType.HandleOffTopic => true,
                NoToolsAgentResponseType.Exit => true,
                _ => false
            };

            if (!isValid)
            {
                action = default;
                return false;
            }

            action = new NoToolsAgentResponse(actionType, message, sql, purpose);
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

    private static bool TryParseWireValue(string? value, out NoToolsAgentResponseType actionType)
    {
        switch (value)
        {
            case "respond":
                actionType = NoToolsAgentResponseType.Respond;
                return true;
            case "run_sql":
                actionType = NoToolsAgentResponseType.RunSql;
                return true;
            case "investigate_sql":
                actionType = NoToolsAgentResponseType.InvestigateSql;
                return true;
            case "handle_offtopic":
                actionType = NoToolsAgentResponseType.HandleOffTopic;
                return true;
            case "exit":
                actionType = NoToolsAgentResponseType.Exit;
                return true;
            default:
                actionType = default;
                return false;
        }
    }

    private static string ToWireValue(NoToolsAgentResponseType actionType)
    {
        return actionType switch
        {
            NoToolsAgentResponseType.Respond => "respond",
            NoToolsAgentResponseType.RunSql => "run_sql",
            NoToolsAgentResponseType.InvestigateSql => "investigate_sql",
            NoToolsAgentResponseType.HandleOffTopic => "handle_offtopic",
            NoToolsAgentResponseType.Exit => "exit",
            _ => throw new ArgumentOutOfRangeException(nameof(actionType), actionType, null)
        };
    }
}
