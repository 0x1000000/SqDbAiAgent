using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public readonly record struct LlmResponse(LlmResponseType RespType, string Text)
{

    public static readonly JsonElement JsonSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "respType": {
              "type": "string",
              "enum": ["t-sql code", "dbInfo", "warning"]
            },
            "text": {
              "type": "string"
            }
          },
          "required": ["respType", "text"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public string ToJsonString()
    {
        return JsonSerializer.Serialize(new
        {
            respType = ToWireValue(this.RespType),
            text = this.Text
        });
    }

    public static bool TryParseFromJson(string json, out LlmResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                response = default;
                return false;
            }

            if (!TryGetPropertyIgnoreCase(document.RootElement, "respType", out var respTypeElement)
                || respTypeElement.ValueKind != JsonValueKind.String
                || !TryParseWireValue(respTypeElement.GetString(), out var responseType))
            {
                response = default;
                return false;
            }

            if (!TryGetPropertyIgnoreCase(document.RootElement, "text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                response = default;
                return false;
            }

            response = new LlmResponse
            {
                RespType = responseType,
                Text = textElement.GetString() ?? string.Empty
            };
            return true;
        }
        catch (JsonException)
        {
            response = default;
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

    private static bool TryParseWireValue(string? value, out LlmResponseType responseType)
    {
        switch (value)
        {
            case "t-sql code":
                responseType = LlmResponseType.TSqlCode;
                return true;
            case "dbInfo":
                responseType = LlmResponseType.DbInfo;
                return true;
            case "warning":
                responseType = LlmResponseType.Warning;
                return true;
            default:
                responseType = default;
                return false;
        }
    }

    private static string ToWireValue(LlmResponseType responseType)
    {
        return responseType switch
        {
            LlmResponseType.TSqlCode => "t-sql code",
            LlmResponseType.DbInfo => "dbInfo",
            LlmResponseType.Warning => "warning",
            _ => throw new ArgumentOutOfRangeException(nameof(responseType), responseType, null)
        };
    }
}
