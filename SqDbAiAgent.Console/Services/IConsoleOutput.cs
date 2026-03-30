namespace SqDbAiAgent.ConsoleApp.Services;

public interface IConsoleOutput
{
    Task<string> ReadUserInput(string? prompt);

    void OutData(string text);

    void OutDataLine(string text);

    void OutError(string text);

    void OutErrorLine(string text);

    void OutDebug(string text);

    void OutDebugLine(string text);
}
