using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace SqDbAiAgent.ConsoleApp.Services.Logging;

public static class LoggingExtensions
{
    public static ILoggingBuilder AddJsonlFile(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string mode)
    {
        var options = configuration.GetSection(FileLoggingOptions.SectionName)
            .Get<FileLoggingOptions>() ?? new FileLoggingOptions();
        if (string.IsNullOrWhiteSpace(options.Path)) return logging;

        var secrets = new[]
        {
            configuration[$"{AppConfig.SectionName}:ConnectionString"],
            configuration[$"{OpenRouterOptions.SectionName}:ApiKey"],
            configuration[$"{McpHttpOptions.SectionName}:ApiKey"]
        };
        logging.Services.AddSingleton(options);
        logging.Services.AddSingleton<ILoggerProvider>(_ => new JsonlLoggerProvider(options, mode, secrets));
        logging.AddFilter<JsonlLoggerProvider>((_, level) => level >= options.MinimumLevel);
        return logging;
    }
}
