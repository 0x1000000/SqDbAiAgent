using SqDbAiAgent.ConsoleApp.Models;

namespace SqDbAiAgent.ConsoleApp.Services;

public interface IMessageAnalyzeSession
{
    Task<MessageClassificationResult?> ClassifyAsync(
        IReadOnlyList<ChatMessage> oldMessages,
        string newMessage,
        CancellationToken cancellationToken = default);

    Task<NewTopicCheckResult?> CheckNewTopicAsync(
        IReadOnlyList<ChatMessage> oldMessages,
        string newMessage,
        CancellationToken cancellationToken = default);
}
