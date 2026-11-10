using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Support.Api.Modules.Agents.Application;

namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

internal static class ModelProviderRegistration
{
    /// <summary>
    /// DI key of the raw provider client (fake or Claude). Everything else uses the unkeyed <see cref="IChatClient"/>,
    /// which is this client wrapped with model-level telemetry. Tests swap the keyed client so they stay instrumented.
    /// </summary>
    public const string ProviderClientKey = "model-provider";

    /// <summary>Default output cap for live calls until L15 introduces per-turn budgets.</summary>
    private const int DefaultMaxTokens = 4096;

    public static IServiceCollection AddModelProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ModelProviderOptions>()
            .Bind(configuration.GetSection(ModelProviderOptions.SectionName))
            .PostConfigure(options =>
            {
                // Fall back to the provider's standard environment variable.
                if (string.IsNullOrWhiteSpace(options.Anthropic.ApiKey))
                {
                    options.Anthropic.ApiKey = configuration[ModelProviderOptions.ApiKeyEnvironmentVariable];
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ModelProviderOptions>, ModelProviderOptionsValidator>();

        // Provider boundary: Fake mode gets the deterministic fake model; Live mode gets Claude.
        // The live client is created lazily from validated options and makes no call when created.
        services.AddKeyedSingleton<IChatClient>(ProviderClientKey, (provider, _) =>
        {
            var options = provider.GetRequiredService<IOptions<ModelProviderOptions>>().Value;
            if (options.Mode != ModelMode.Live)
            {
                return new FakeChatClient();
            }

            var client = new AnthropicClient { ApiKey = options.Anthropic.ApiKey };
            return client.AsIChatClient(options.Anthropic.Model!, DefaultMaxTokens);
        });

        // Model layer telemetry: one "chat" span and per-call token usage for every model call, on Support.Model.
        // Because the client is already instrumented, MAF's agent telemetry does not auto-wire a second copy.
        // (execute_tool spans are emitted on the agent source, Support.Agents; observed in L06's telemetry tests.)
        services.AddSingleton<IChatClient>(provider =>
        {
            var captureSensitiveData = provider.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value.CaptureSensitiveData;
            return provider.GetRequiredKeyedService<IChatClient>(ProviderClientKey)
                .AsBuilder()
                .UseOpenTelemetry(sourceName: AgentTelemetry.ModelSourceName, configure: client => client.EnableSensitiveData = captureSensitiveData)
                .Build(provider);
        });

        services.AddHealthChecks().AddCheck<ModelProviderHealthCheck>("model-provider");

        return services;
    }
}
