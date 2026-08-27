using Microsoft.Extensions.Options;

namespace SqDbAiAgent.ConsoleApp.Services.Llm;

public sealed class LlmInteractionLoggerService
{
    private readonly string? _logFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(this._logFilePath);

    public LlmInteractionLoggerService(IOptions<AppConfig> appConfig)
    {
        this._logFilePath = string.IsNullOrWhiteSpace(appConfig.Value.LlmLogFilePath)
            ? null
            : appConfig.Value.LlmLogFilePath;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (!this.IsEnabled)
        {
            return;
        }

        var directory = Path.GetDirectoryName(this._logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await this._lock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(this._logFilePath!, string.Empty, cancellationToken);
        }
        finally
        {
            this._lock.Release();
        }
    }

    public async Task LogAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!this.IsEnabled)
        {
            return;
        }

        await this._lock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(this._logFilePath!, text + Environment.NewLine, cancellationToken);
        }
        finally
        {
            this._lock.Release();
        }
    }
}
