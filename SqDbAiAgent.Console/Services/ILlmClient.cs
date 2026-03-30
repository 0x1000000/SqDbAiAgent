using System.Text.Json;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface ILlmClient
{
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    Task<string> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        CancellationToken cancellationToken = default);
}
