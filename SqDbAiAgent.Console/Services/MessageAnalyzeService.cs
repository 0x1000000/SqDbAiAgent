using Microsoft.Extensions.Options;
using SqDbAiAgent.ConsoleApp.Models;
using SqExpress;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class MessageAnalyzeService(
    ILlmClient llmClient,
    IOptions<AppConfig> appConfig,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<OpenRouterOptions> openRouterOptions)
    : IMessageAnalyzeService
{
    private readonly AppConfig _appConfig = appConfig.Value;
    private readonly OllamaOptions _ollamaOptions = ollamaOptions.Value;
    private readonly OpenRouterOptions _openRouterOptions = openRouterOptions.Value;

    public IMessageAnalyzeSession CreateSession(
        IConsoleOutput output,
        string databaseName,
        IReadOnlyList<TableBase> publicTables,
        string analyzerSchemaPrompt)
    {
        return new MessageAnalyzeSession(
            llmClient,
            output,
            this._appConfig,
            GetConfiguredModel(this._appConfig, this._ollamaOptions, this._openRouterOptions),
            publicTables,
            analyzerSchemaPrompt,
            databaseName
        );
    }

    private static string GetConfiguredModel(AppConfig appConfig, OllamaOptions ollamaOptions, OpenRouterOptions openRouterOptions)
    {
        return string.Equals(appConfig.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase)
            ? openRouterOptions.Model
            : ollamaOptions.Model;
    }
}
