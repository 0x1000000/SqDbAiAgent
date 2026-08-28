using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

public static class McpServerHost
{
    public static async Task<int> RunHttpAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureFiles(builder.Configuration, args);
        var configuredHttp = builder.Configuration.GetSection(McpHttpOptions.SectionName)
            .Get<McpHttpOptions>() ?? new McpHttpOptions();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonlFile(builder.Configuration, "McpHttp");

        builder.Services.AddOptions<McpHttpOptions>()
            .Bind(builder.Configuration.GetSection(McpHttpOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.Url, UriKind.Absolute, out _), "McpHttp Url must be a valid absolute URI.")
            .Validate(options => IsUsableApiKey(options.ApiKey), "McpHttp ApiKey must be configured and must not use the sample placeholder.")
            .ValidateOnStart();
        builder.WebHost.UseUrls(configuredHttp.Url);
        var databaseName = GetConfiguredDatabaseName(builder.Configuration);
        var hasSecurityProfile = new SecurityFilterFactoryService().HasSecurityProfile(databaseName);
        AddSharedServices(builder.Services, builder.Configuration, McpTransport.Http, 0, databaseName, hasSecurityProfile);
        var mcpBuilder = builder.Services.AddMcpServer(
                options => ConfigureServer(options, McpTransport.Http, 0, databaseName, hasSecurityProfile))
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<McpDatabaseTools>()
            .WithPrompts<McpDatabasePrompts>()
            .WithResources<McpDatabaseResources>();
        if (hasSecurityProfile)
        {
            mcpBuilder.WithTools<McpSecurityTools>();
        }

        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SqDbAiAgent.McpHttp");
        if (!IsUsableApiKey(configuredHttp.ApiKey))
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarningEvent("McpStartupRefused", ("reason", "A non-placeholder API key is required."));
            return 1;
        }
        app.Use(async (httpContext, next) =>
        {
            if (!httpContext.Request.Path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }

            var authorization = httpContext.Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            var suppliedKey = authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? authorization[bearerPrefix.Length..].Trim()
                : string.Empty;
            if (!FixedTimeEquals(suppliedKey, configuredHttp.ApiKey))
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await httpContext.Response.WriteAsync("Unauthorized.", cancellationToken);
                return;
            }

            await next();
        });
        app.MapMcp("/mcp");

        try
        {
            await app.Services.GetRequiredService<DatabaseContextService>().GetAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformationEvent("McpServerStarted", ("transport", "Http"),
                    ("url", configuredHttp.Url.TrimEnd('/') + "/mcp"));
            await app.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogErrorEvent("McpServerFailed", ex, ("transport", "Http"));
            return 1;
        }
    }

    public static async Task<int> RunStdioAsync(string[] args, int databaseUserId, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureFiles(builder.Configuration, args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonlFile(builder.Configuration, "McpStdio");
        var databaseName = GetConfiguredDatabaseName(builder.Configuration);
        var hasSecurityProfile = new SecurityFilterFactoryService().HasSecurityProfile(databaseName);
        AddSharedServices(builder.Services, builder.Configuration, McpTransport.Stdio, databaseUserId, databaseName, hasSecurityProfile);
        var mcpBuilder = builder.Services.AddMcpServer(
                options => ConfigureServer(options, McpTransport.Stdio, databaseUserId, databaseName, hasSecurityProfile))
            .WithStdioServerTransport()
            .WithTools<McpDatabaseTools>()
            .WithPrompts<McpDatabasePrompts>()
            .WithResources<McpDatabaseResources>();
        if (hasSecurityProfile)
        {
            mcpBuilder.WithTools<McpSecurityTools>();
        }

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SqDbAiAgent.McpStdio");
        try
        {
            var context = await host.Services.GetRequiredService<DatabaseContextService>().GetAsync(cancellationToken);
            if (databaseUserId > 0 && !context.SecurityUsers.ContainsKey(databaseUserId))
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    logger.LogWarningEvent("McpStartupRefused",
                        ("reason", "The configured database user ID was not returned by list_security_users."),
                        ("databaseUser", databaseUserId));
                return 1;
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformationEvent("McpServerStarted", ("transport", "Stdio"),
                    ("databaseUser", databaseUserId));
            await host.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                logger.LogErrorEvent("McpServerFailed", ex, ("transport", "Stdio"));
            return 1;
        }
    }

    private static void AddSharedServices(
        IServiceCollection services,
        IConfiguration configuration,
        McpTransport transport,
        int databaseUserId,
        string databaseName,
        bool hasSecurityProfile)
    {
        services.AddOptions<AppConfig>()
            .Bind(configuration.GetSection(AppConfig.SectionName))
            .ValidateDataAnnotations()
            .Validate(config => !string.IsNullOrWhiteSpace(config.ConnectionString), "App ConnectionString must be provided.")
            .ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConsoleOutput, NullConsoleOutputService>();
        services.AddSingleton<SecurityFilterFactoryService>();
        services.AddSingleton<DatabaseContextService>();
        services.AddSingleton<TableResultFormatterService>();
        services.AddSingleton(serviceProvider => new McpRuntimeContextService(
            transport,
            databaseUserId,
            databaseName,
            hasSecurityProfile,
            serviceProvider.GetRequiredService<IHttpContextAccessor>()));
        services.AddSingleton<McpAgentInstructionsProviderService>();
        services.AddSingleton<McpDatabaseService>();
    }

    private static void ConfigureFiles(ConfigurationManager configuration, string[] args) =>
        configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

    private static void ConfigureServer(
        ModelContextProtocol.Server.McpServerOptions options,
        McpTransport transport,
        int databaseUserId,
        string databaseName,
        bool hasSecurityProfile)
    {
        options.ServerInfo = new() { Name = "SqDbAiAgent", Version = "1.0.0" };
        options.ServerInstructions = McpAgentInstructionsProviderService.Build(
            transport,
            databaseUserId,
            databaseName,
            hasSecurityProfile);
    }

    private static string GetConfiguredDatabaseName(IConfiguration configuration)
    {
        var connectionString = configuration[$"{AppConfig.SectionName}:ConnectionString"];
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("The connection string must specify a database name.");
        }

        return builder.InitialCatalog;
    }

    private static bool IsUsableApiKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey)
        && !string.Equals(apiKey, McpHttpOptions.ApiKeyPlaceholder, StringComparison.Ordinal);

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}
