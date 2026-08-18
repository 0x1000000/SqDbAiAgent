using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed record LlmToolDefinition(string Name, string Description, JsonElement Parameters);
