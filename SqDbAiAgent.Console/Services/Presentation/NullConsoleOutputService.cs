namespace SqDbAiAgent.ConsoleApp.Services.Presentation;

public sealed class NullConsoleOutputService : IConsoleOutput
{
    public Task<string?> ReadUserInput(string? prompt) => Task.FromResult<string?>(null);
    public void OutData(string text) { }
    public void OutDataLine(string text) { }
    public void OutError(string text) { }
    public void OutErrorLine(string text) { }
    public void OutDebug(string text) { }
    public void OutDebugLine(string text) { }
}
