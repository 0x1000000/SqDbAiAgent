namespace SqDbAiAgent.ConsoleApp.Services;

public interface IDbChatSession
{
    Task<bool> HandleInputAsync(string userRequest, CancellationToken cancellationToken = default);
}
