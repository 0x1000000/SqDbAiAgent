using System.Text.Json;
using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface ILlmClient
{
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    Task<LlmModelCapabilities> GetModelCapabilitiesAsync(
        string model,
        CancellationToken cancellationToken = default);

    Task<LlmChatResult> ChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        JsonElement? format = null,
        LlmThinkLevel thinkLevel = LlmThinkLevel.Default,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
