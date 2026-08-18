using System.Text.Json;

namespace SqDbAiAgent.ConsoleApp.Models;

public sealed record LlmToolCall(string Id, string Name, JsonElement Arguments);
