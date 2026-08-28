using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Models.Configuration;

public sealed class FileLoggingOptions
{
    public const string SectionName = "Logging:File";

    public string? Path { get; init; }

    public LogLevel MinimumLevel { get; init; } = LogLevel.Warning;

    public int RetainedDays { get; init; } = 7;

    public FileLogFormat Format { get; init; } = FileLogFormat.PlainText;
}

public enum FileLogFormat
{
    PlainText,
    Jsonl
}
