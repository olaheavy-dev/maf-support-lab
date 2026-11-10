using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Support.Api.Tests.Host;

public sealed class StartupTests
{
    private const string PlantedSecret = "sk-ant-test-planted-secret-value-0000";

    [Fact]
    public async Task Fake_mode_starts_without_a_provider_key_and_reports_healthy()
    {
        await using var factory = SupportApiFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Live_mode_without_a_key_refuses_to_start_and_names_the_missing_setting()
    {
        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
        });

        var error = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("ANTHROPIC_API_KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_mode_error_never_contains_the_configured_secret()
    {
        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = string.Empty,
            ["ModelProvider:Anthropic:ApiKey"] = PlantedSecret,
        });

        var error = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("ModelProvider:Anthropic:Model", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_mode_with_complete_settings_creates_the_Claude_client_without_calling_it()
    {
        await using var factory = SupportApiFactory.Create(new Dictionary<string, string?>
        {
            ["ModelProvider:Mode"] = "Live",
            ["ModelProvider:Anthropic:Model"] = "claude-haiku-5-5",
            ["ModelProvider:Anthropic:ApiKey"] = PlantedSecret,
        });
        using var client = factory.CreateClient();

        var chatClient = factory.Services.GetRequiredService<IChatClient>();

        Assert.NotNull(chatClient.GetService<ChatClientMetadata>());
        var health = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
