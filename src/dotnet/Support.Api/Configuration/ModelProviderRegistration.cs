using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Support.Api.Configuration;

internal static class ModelProviderRegistration
{
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

        // The live Claude client is created lazily from validated options and makes no call when created.
        // Fake mode has no model client yet: L05 adds the controllable fake client at this boundary.
        services.AddSingleton<IChatClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ModelProviderOptions>>().Value;
            if (options.Mode != ModelMode.Live)
            {
                throw new InvalidOperationException("No model client is registered in Fake mode yet; L05 adds the fake client.");
            }

            var client = new AnthropicClient { ApiKey = options.Anthropic.ApiKey };
            return client.AsIChatClient(options.Anthropic.Model!, DefaultMaxTokens);
        });

        services.AddHealthChecks().AddCheck<ModelProviderHealthCheck>("model-provider");

        return services;
    }
}
