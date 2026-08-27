using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models.Llm;

public sealed record LlmToolCall(string Id, string Name, JsonElement Arguments);
