namespace SqDbAiAgent.ConsoleApp.Services;

public interface ILlmInteractionLogger
{
    bool IsEnabled { get; }

    Task ResetAsync(CancellationToken cancellationToken = default);

    Task LogAsync(string text, CancellationToken cancellationToken = default);
}
