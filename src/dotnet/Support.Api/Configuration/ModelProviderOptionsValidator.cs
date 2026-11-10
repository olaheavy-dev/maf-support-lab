using Microsoft.Extensions.Options;

namespace Support.Api.Configuration;

/// <summary>
/// Live mode must have a model and an API key. Messages name the missing setting only:
/// they never include configured values, so a secret cannot leak through a startup error.
/// </summary>
internal sealed class ModelProviderOptionsValidator : IValidateOptions<ModelProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelProviderOptions options)
    {
        if (options.Mode != ModelMode.Live)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Anthropic.Model))
        {
            failures.Add("ModelProvider:Anthropic:Model is required when ModelProvider:Mode is Live.");
        }

        if (string.IsNullOrWhiteSpace(options.Anthropic.ApiKey))
        {
            failures.Add(
                $"An Anthropic API key is required when ModelProvider:Mode is Live. Set {ModelProviderOptions.ApiKeyEnvironmentVariable} or the user-secret ModelProvider:Anthropic:ApiKey.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
