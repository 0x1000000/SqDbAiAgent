namespace SqDbAiAgent.ConsoleApp.Services;

public interface IChatService
{
    Task RunAsync(IConsoleOutput output, CancellationToken cancellationToken = default);
}
