namespace SqDbAiAgent.ConsoleApp.Services.Llm;

public sealed class ToolCallingResolverService(ILlmClient llmClient)
{
    private readonly Dictionary<string, LlmModelCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ToolCallingResolution> ResolveAsync(
        ToolCallingMode requestedMode,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (requestedMode == ToolCallingMode.Disabled)
        {
            return new ToolCallingResolution(requestedMode, false, false);
        }

        if (!this._capabilities.TryGetValue(model, out var capabilities))
        {
            try
            {
                capabilities = await llmClient.GetModelCapabilitiesAsync(model, cancellationToken);
            }
            catch (Exception ex) when (requestedMode == ToolCallingMode.Auto && ex is not OperationCanceledException)
            {
                capabilities = new LlmModelCapabilities(false);
            }
            catch (Exception ex) when (requestedMode == ToolCallingMode.Enabled && ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Tool calling is enabled, but capabilities for model '{model}' could not be determined.",
                    ex);
            }

            this._capabilities[model] = capabilities;
        }

        if (requestedMode == ToolCallingMode.Enabled && !capabilities.SupportsTools)
        {
            throw new InvalidOperationException(
                $"Tool calling is enabled, but model '{model}' does not advertise native tool support.");
        }

        return new ToolCallingResolution(requestedMode, capabilities.SupportsTools, capabilities.SupportsTools);
    }
}

public sealed record ToolCallingResolution(
    ToolCallingMode RequestedMode,
    bool ModelSupportsTools,
    bool UseNativeTools);
