using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Models;
using SqDbAiAgent.ConsoleApp.Services;

namespace SqDbAiAgent.ConsoleApp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddOptions<AppConfig>()
            .Bind(builder.Configuration.GetSection(AppConfig.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                config => !string.IsNullOrWhiteSpace(config.ConnectionString),
                "App ConnectionString must be provided.")
            .Validate(
                config => IsSupportedProvider(config.LlmProvider),
                "App LlmProvider must be either 'Ollama' or 'OpenRouter'.");

        builder.Services
            .AddOptions<OllamaOptions>()
            .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "Ollama BaseUrl must be a valid absolute URI.");

        builder.Services
            .AddOptions<OpenRouterOptions>()
            .Bind(builder.Configuration.GetSection(OpenRouterOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "OpenRouter BaseUrl must be a valid absolute URI.");

        builder.Services
            .AddHttpClient<OllamaClient>((serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
                httpClient.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
                httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

        builder.Services
            .AddHttpClient<OpenRouterClient>((serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
                httpClient.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
                httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

        builder.Services.AddSingleton<ILlmClient>(serviceProvider =>
        {
            var appConfig = serviceProvider.GetRequiredService<IOptions<AppConfig>>().Value;
            return IsOpenRouter(appConfig.LlmProvider)
                ? serviceProvider.GetRequiredService<OpenRouterClient>()
                : serviceProvider.GetRequiredService<OllamaClient>();
        });

        builder.Services.AddSingleton<IConsoleOutput, ConsoleOutput>();
        builder.Services.AddSingleton<ILlmInteractionLogger, LlmInteractionLogger>();
        builder.Services.AddSingleton<ITablePrinter, ConsoleTablePrinter>();
        builder.Services.AddSingleton<IAgentTableFormatter>(serviceProvider =>
            (ConsoleTablePrinter)serviceProvider.GetRequiredService<ITablePrinter>());
        builder.Services.AddSingleton<IMessageAnalyzeService, MessageAnalyzeService>();
        builder.Services.AddSingleton<ISqlApprovalService, SqlApprovalService>();
        builder.Services.AddSingleton<IChatService, DbChatService>();
        builder.Services.AddSingleton<ISecurityFilterFactoryService, DefaultSecurityFilterFactoryService>();

        using var host = builder.Build();

        try
        {
            var appConfig = host.Services.GetRequiredService<IOptions<AppConfig>>().Value;
            var llmClient = host.Services.GetRequiredService<ILlmClient>();
            var ollamaOptions = host.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var openRouterOptions = host.Services.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
            var output = host.Services.GetRequiredService<IConsoleOutput>();
            var chatService = host.Services.GetRequiredService<IChatService>();
            var interactionLogger = host.Services.GetRequiredService<ILlmInteractionLogger>();
            var providerName = appConfig.LlmProvider;
            var baseUrl = IsOpenRouter(providerName) ? openRouterOptions.BaseUrl : ollamaOptions.BaseUrl;
            var configuredModel = IsOpenRouter(providerName) ? openRouterOptions.Model : ollamaOptions.Model;

            await interactionLogger.ResetAsync();

            output.OutDebugLine($"Checking {providerName} at {baseUrl}...");

            var availableModels = await llmClient.GetAvailableModelsAsync();

            output.OutDebugLine($"Connected to {providerName}.");

            if (availableModels.Count == 0)
            {
                output.OutErrorLine($"No models are available from {providerName}.");
                return 1;
            }

            output.OutDebugLine("Installed models:");
            foreach (var model in availableModels.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                output.OutDebugLine($" - {model}");
            }

            if (!availableModels.Contains(configuredModel, StringComparer.OrdinalIgnoreCase))
            {
                output.OutDebugLine(string.Empty);
                output.OutErrorLine($"Configured model '{configuredModel}' was not found.");
                return 1;
            }

            output.OutDebugLine(string.Empty);
            output.OutDebugLine($"Configured model '{configuredModel}' is available.");
            output.OutDebugLine(string.Empty);
            output.OutDebugLine("Checking database connection...");

            try
            {
                var databaseName = GetRequiredDatabaseName(appConfig.ConnectionString);
                await ValidateDatabaseConnectionAsync(appConfig.ConnectionString);
                output.OutDebugLine($"Connected to database '{databaseName}'.");
                output.OutDebugLine(string.Empty);
            }
            catch (Exception ex)
            {
                output.OutErrorLine(
                    TryBuildFriendlyDatabaseMessage(appConfig.ConnectionString, ex)
                    ?? $"Database connectivity check failed: {ex.Message}"
                );
                return 1;
            }

            await chatService.RunAsync(output);

            return 0;
        }
        catch (Exception ex)
        {
            var friendlyMessage = TryBuildFriendlyStartupMessage(ex);
            Console.Error.WriteLine(friendlyMessage ?? $"Application failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task ValidateDatabaseConnectionAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
    }

    private static string GetRequiredDatabaseName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("The connection string must specify a database name.");
        }

        return builder.InitialCatalog;
    }

    private static string? TryBuildFriendlyStartupMessage(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException httpRequestException
                && httpRequestException.InnerException is System.Net.Sockets.SocketException socketException
                && socketException.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                return "Could not find a working Ollama server on localhost:11434. The server may be unavailable or the configuration is not correct.";
            }
        }

        return null;
    }

    private static bool IsSupportedProvider(string? provider)
    {
        return string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
               || string.Equals(provider, "OpenRouter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenRouter(string? provider)
    {
        return string.Equals(provider, "OpenRouter", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSlash(string url)
    {
        return string.IsNullOrEmpty(url) || url.EndsWith("/", StringComparison.Ordinal)
            ? url
            : url + "/";
    }

    private static string? TryBuildFriendlyDatabaseMessage(string connectionString, Exception exception)
    {
        if (exception is InvalidOperationException invalidOperationException
            && string.Equals(
                invalidOperationException.Message,
                "The connection string must specify a database name.",
                StringComparison.Ordinal))
        {
            return "The connection string must specify a database name.";
        }

        SqlException? sqlException = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException currentSqlException)
            {
                sqlException = currentSqlException;
                break;
            }
        }

        if (sqlException is null)
        {
            return null;
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        var dataSource = string.IsNullOrWhiteSpace(builder.DataSource) ? "(unknown server)" : builder.DataSource;
        var databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "(unknown database)" : builder.InitialCatalog;

        return $"Could not connect to the database server '{dataSource}' or open database '{databaseName}'. The server may be unavailable, the database may not exist, or the configuration is not correct.";
    }
}
