using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Logging;

public static class StructuredLoggingExtensions
{
    public static void LogDebugEvent(this ILogger logger, string eventName,
        params ReadOnlySpan<(string Name, object? Value)> fields) =>
        Write(logger, LogLevel.Debug, eventName, null, fields);

    public static void LogInformationEvent(this ILogger logger, string eventName,
        params ReadOnlySpan<(string Name, object? Value)> fields) =>
        Write(logger, LogLevel.Information, eventName, null, fields);

    public static void LogWarningEvent(this ILogger logger, string eventName,
        params ReadOnlySpan<(string Name, object? Value)> fields) =>
        Write(logger, LogLevel.Warning, eventName, null, fields);

    public static void LogErrorEvent(this ILogger logger, string eventName, Exception exception,
        params ReadOnlySpan<(string Name, object? Value)> fields) =>
        Write(logger, LogLevel.Error, eventName, exception, fields);

    private static void Write(ILogger logger, LogLevel level, string eventName, Exception? exception,
        ReadOnlySpan<(string Name, object? Value)> fields)
    {
        if (!logger.IsEnabled(level)) return;
        var state = new KeyValuePair<string, object?>[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            state[index] = new KeyValuePair<string, object?>(fields[index].Name, fields[index].Value);
        }
        logger.Log(level, new EventId(0, eventName), state, exception, static (_, _) => string.Empty);
    }
}
