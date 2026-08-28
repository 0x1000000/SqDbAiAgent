using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;

namespace SqDbAiAgent.ConsoleApp.Services.Logging;

public sealed class JsonlLoggerProvider : ILoggerProvider
{
    private const string DateToken = "{date}";
    private readonly FileLoggingOptions _options;
    private readonly string _mode;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string[] _encodedSecrets;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonlLogger> _loggers = new(StringComparer.Ordinal);
    private DateOnly? _lastCleanupDate;

    public JsonlLoggerProvider(FileLoggingOptions options, string mode, IEnumerable<string?>? secrets = null)
    {
        this._options = options;
        this._mode = mode;
        this._encodedSecrets = secrets?
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Select(secret => JsonEncodedText.Encode(secret!).ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToArray() ?? [];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(this._options.Path);

    public ILogger CreateLogger(string categoryName) =>
        this._loggers.GetOrAdd(categoryName, category => new JsonlLogger(this, category));

    public void Dispose() => this._writeLock.Dispose();

    internal bool IsEnabled(LogLevel level) =>
        this.IsConfigured && level != LogLevel.None && level >= this._options.MinimumLevel;

    internal void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(level)) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["timestamp"] = now.ToString("O"),
                ["level"] = level.ToString(),
                ["event"] = eventId.Name ?? (eventId.Id == 0 ? "Log" : eventId.Id.ToString()),
                ["category"] = category,
                ["mode"] = this._mode,
                ["processId"] = Environment.ProcessId,
                ["sessionId"] = this._sessionId
            };

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    if (value.Key != "{OriginalFormat}" && !IsSecretField(value.Key))
                    {
                        fields[value.Key] = value.Value;
                    }
                }
            }

            var message = formatter(state, exception);
            if (!string.IsNullOrWhiteSpace(message)) fields["message"] = message;
            if (exception is not null)
            {
                fields["exceptionType"] = exception.GetType().FullName;
                fields["exceptionMessage"] = exception.Message;
                fields["exception"] = exception.ToString();
            }

            var line = this._options.Format == FileLogFormat.Jsonl
                ? JsonSerializer.Serialize(fields, SerializerOptions)
                : FormatPlainText(fields);
            foreach (var secret in this._encodedSecrets)
            {
                line = line.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
            line += Environment.NewLine;
            this.WriteLineAsync(now, line).GetAwaiter().GetResult();
        }
        catch
        {
            // Logging must never interrupt application behavior.
        }
    }

    private async Task WriteLineAsync(DateTimeOffset now, string line)
    {
        await this._writeLock.WaitAsync();
        try
        {
            var filePath = this.ResolvePath(now);
            var directory = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(filePath, line, Utf8NoBom);
            this.Cleanup(now, filePath);
        }
        finally
        {
            this._writeLock.Release();
        }
    }

    private string ResolvePath(DateTimeOffset now)
    {
        var configured = this._options.Path!.Trim();
        var dated = configured.Contains(DateToken, StringComparison.OrdinalIgnoreCase)
            ? configured.Replace(DateToken, now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            : InsertDate(configured, now.ToString("yyyyMMdd"));
        return System.IO.Path.IsPathRooted(dated)
            ? System.IO.Path.GetFullPath(dated)
            : System.IO.Path.GetFullPath(dated, AppContext.BaseDirectory);
    }

    private void Cleanup(DateTimeOffset now, string currentFile)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (this._lastCleanupDate == today || this._options.RetainedDays < 1) return;
        this._lastCleanupDate = today;

        var configured = this._options.Path!.Trim();
        var absolutePattern = System.IO.Path.IsPathRooted(configured)
            ? System.IO.Path.GetFullPath(configured)
            : System.IO.Path.GetFullPath(configured, AppContext.BaseDirectory);
        var directory = System.IO.Path.GetDirectoryName(absolutePattern);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var fileName = System.IO.Path.GetFileName(absolutePattern);
        var searchPattern = fileName.Contains(DateToken, StringComparison.OrdinalIgnoreCase)
            ? fileName.Replace(DateToken, "*", StringComparison.OrdinalIgnoreCase)
            : InsertDate(fileName, "*");
        var cutoff = now.UtcDateTime.Date.AddDays(-this._options.RetainedDays + 1);
        foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(file, currentFile, StringComparison.OrdinalIgnoreCase)
                && File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static string InsertDate(string path, string date)
    {
        var extension = System.IO.Path.GetExtension(path);
        return string.IsNullOrEmpty(extension)
            ? path + "-" + date
            : path[..^extension.Length] + "-" + date + extension;
    }

    private static bool IsSecretField(string key) =>
        key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
        || key.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Token", StringComparison.OrdinalIgnoreCase);

    private static string FormatPlainText(IReadOnlyDictionary<string, object?> fields)
    {
        var builder = new StringBuilder();
        builder.Append(Convert.ToString(fields["timestamp"], CultureInfo.InvariantCulture));
        builder.Append(" [").Append(fields["level"]).Append("] ");
        builder.Append(fields["event"]);
        builder.Append(" category=").Append(JsonSerializer.Serialize(fields["category"], SerializerOptions));
        builder.Append(" mode=").Append(JsonSerializer.Serialize(fields["mode"], SerializerOptions));
        builder.Append(" processId=").Append(fields["processId"]);
        builder.Append(" sessionId=").Append(fields["sessionId"]);

        var hasMultilineField = false;
        foreach (var field in fields)
        {
            if (StandardFieldNames.Contains(field.Key)) continue;
            if (IsSqlField(field.Key) && field.Value is string sql && ContainsLineBreak(sql))
            {
                builder.AppendLine().Append("  ").Append(field.Key).Append(':');
                foreach (var line in NormalizeLineBreaks(sql).Split('\n'))
                {
                    builder.AppendLine().Append("    ").Append(line);
                }
                hasMultilineField = true;
                continue;
            }

            builder.Append(hasMultilineField ? Environment.NewLine + "  " : " ");
            builder.Append(field.Key).Append('=');
            builder.Append(JsonSerializer.Serialize(field.Value, SerializerOptions));
        }
        return builder.ToString();
    }

    private static bool IsSqlField(string name) =>
        string.Equals(name, "sql", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Sql", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\r') || value.Contains('\n');

    private static string NormalizeLineBreaks(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed class JsonlLogger(JsonlLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Write(category, logLevel, eventId, state, exception, formatter);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly HashSet<string> StandardFieldNames = new(StringComparer.Ordinal)
    {
        "timestamp", "level", "event", "category", "mode", "processId", "sessionId"
    };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
