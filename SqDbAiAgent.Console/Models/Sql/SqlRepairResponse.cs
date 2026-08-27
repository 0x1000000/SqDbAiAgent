using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models.Sql;

public readonly record struct SqlRepairResponse(SqlRepairResponseType RespType, string Text)
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

    public static bool TryParseFromJson(string json, out SqlRepairResponse response)
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

            response = new SqlRepairResponse
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

    private static bool TryParseWireValue(string? value, out SqlRepairResponseType responseType)
    {
        switch (value)
        {
            case "t-sql code":
                responseType = SqlRepairResponseType.TSqlCode;
                return true;
            case "dbInfo":
                responseType = SqlRepairResponseType.DbInfo;
                return true;
            case "warning":
                responseType = SqlRepairResponseType.Warning;
                return true;
            default:
                responseType = default;
                return false;
        }
    }

    private static string ToWireValue(SqlRepairResponseType responseType)
    {
        return responseType switch
        {
            SqlRepairResponseType.TSqlCode => "t-sql code",
            SqlRepairResponseType.DbInfo => "dbInfo",
            SqlRepairResponseType.Warning => "warning",
            _ => throw new ArgumentOutOfRangeException(nameof(responseType), responseType, null)
        };
    }
}
