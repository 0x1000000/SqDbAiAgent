using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models.Llm;

public sealed record LlmToolDefinition(string Name, string Description, JsonElement Parameters);
