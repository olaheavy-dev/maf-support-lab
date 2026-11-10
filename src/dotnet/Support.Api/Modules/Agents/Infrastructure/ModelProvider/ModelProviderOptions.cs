namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

/// <summary>Which model provider the API uses. Fake needs no credentials and makes no billable calls.</summary>
public enum ModelMode
{
    Fake,
    Live,
}

/// <summary>Bound from the "ModelProvider" configuration section.</summary>
public sealed class ModelProviderOptions
{
    public const string SectionName = "ModelProvider";

    /// <summary>Standard environment variable read as a fallback for the API key.</summary>
    public const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";

    public ModelMode Mode { get; set; } = ModelMode.Fake;

    public AnthropicSettings Anthropic { get; set; } = new();
}

public sealed class AnthropicSettings
{
    /// <summary>Model id, for example claude-haiku-5-5. Configured, never hard-coded in behavior.</summary>
    public string? Model { get; set; }

    /// <summary>Secret. Supplied only through user-secrets or the environment; never committed or logged.</summary>
    public string? ApiKey { get; set; }
}
