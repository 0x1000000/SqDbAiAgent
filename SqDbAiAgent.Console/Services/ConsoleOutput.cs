namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class ConsoleOutput : IConsoleOutput
{
    public Task<string> ReadUserInput(string? prompt)
    {
        if (!string.IsNullOrEmpty(prompt))
        {
            Console.Write(prompt);
        }
        return Task.FromResult(Console.ReadLine() ?? string.Empty);
    }

    public void OutData(string text)
    {
        WriteWithColor(text, newLine: false, ConsoleColor.Gray);
    }

    public void OutDataLine(string text)
    {
        WriteWithColor(text, newLine: true, ConsoleColor.Gray);
    }

    public void OutError(string text)
    {
        WriteWithColor(text, newLine: false, ConsoleColor.Red);
    }

    public void OutErrorLine(string text)
    {
        WriteWithColor(text, newLine: true, ConsoleColor.Red);
    }

    public void OutDebug(string text)
    {
        WriteWithColor(text, newLine: false, ConsoleColor.DarkGray);
    }

    public void OutDebugLine(string text)
    {
        WriteWithColor(text, newLine: true, ConsoleColor.DarkGray);
    }

    private static void WriteWithColor(string text, bool newLine, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;

        try
        {
            Console.ForegroundColor = color;

            if (newLine)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }
}
