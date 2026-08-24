using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using SqDbAiAgent.ConsoleApp.Models;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace SqDbAiAgent.ConsoleApp.Services;

public sealed class AgentFrameworkChatClientFactory(
    IOptions<AppConfig> appConfig,
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<OpenRouterOptions> openRouterOptions,
    ILlmInteractionLogger logger)
{
    private IChatClient? _chatClient;
    private OpenAIClient? _openRouterClient;
    private OllamaApiClient? _ollamaClient;

    public IChatClient Create()
    {
        if (this._chatClient is not null)
        {
            return this._chatClient;
        }

        if (string.Equals(appConfig.Value.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            var options = openRouterOptions.Value;
            var client = this.CreateOpenRouterClient();
            this._chatClient = new InteractionLoggingChatClient(
                client.GetChatClient(options.Model).AsIChatClient(),
                logger,
                appConfig.Value);
            return this._chatClient;
        }

        this._chatClient = new InteractionLoggingChatClient(
            this.CreateOllamaClient(),
            logger,
            appConfig.Value);
        return this._chatClient;
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(appConfig.Value.LlmProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            var response = await this.CreateOpenRouterClient()
                .GetOpenAIModelClient()
                .GetModelsAsync(cancellationToken);
            return response.Value.Select(model => model.Id).ToList();
        }

        var client = this.CreateOllamaClient();
        var models = await client.ListLocalModelsAsync(cancellationToken);
        return models.Select(model => model.Name).ToList();
    }

    private OpenAIClient CreateOpenRouterClient()
    {
        if (this._openRouterClient is not null)
        {
            return this._openRouterClient;
        }

        var options = openRouterOptions.Value;
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        if (!string.IsNullOrWhiteSpace(options.Referer))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", options.Referer);
        }

        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", options.Title);
        }

        this._openRouterClient = new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
                NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                Transport = new HttpClientPipelineTransport(httpClient)
            });
        return this._openRouterClient;
    }

    private OllamaApiClient CreateOllamaClient()
    {
        if (this._ollamaClient is not null)
        {
            return this._ollamaClient;
        }

        var options = ollamaOptions.Value;
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        this._ollamaClient = new OllamaApiClient(httpClient, options.Model);
        return this._ollamaClient;
    }
}
