using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Support.Api.Tests.Modules.Identity;

public sealed class IdentityTests
{
    [Fact]
    public async Task Development_principal_comes_from_server_configuration()
    {
        await using var factory = SupportApiFactory.Create();
        using var client = factory.CreateClient();

        Assert.Equal("CUST-1001", await WhoAmI(client));
    }

    [Fact]
    public async Task Test_host_can_run_as_two_different_customers()
    {
        await using var ada = SupportApiFactory.Create(principal: "CUST-1001");
        await using var ben = SupportApiFactory.Create(principal: "CUST-1002");

        Assert.Equal("CUST-1001", await WhoAmI(ada.CreateClient()));
        Assert.Equal("CUST-1002", await WhoAmI(ben.CreateClient()));
    }

    [Theory]
    [InlineData("X-Customer-Id")]
    [InlineData("X-User-Id")]
    [InlineData("X-Principal")]
    public async Task Client_headers_cannot_change_the_principal(string header)
    {
        await using var factory = SupportApiFactory.Create();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, "CUST-1002");

        Assert.Equal("CUST-1001", await WhoAmI(client));
    }

    [Fact]
    public async Task Query_string_cannot_change_the_principal()
    {
        await using var factory = SupportApiFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<WhoAmIResponse>(
            new Uri("/dev/whoami?customer_id=CUST-1002", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("CUST-1001", response!.CustomerId);
    }

    [Fact]
    public void Outside_development_there_is_no_principal()
    {
        using var factory = SupportApiFactory.Create(environment: "Production");

        var error = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetService(typeof(Support.Api.Modules.Identity.Application.IPrincipalSource)));

        Assert.Contains("No identity provider", error.Message, StringComparison.Ordinal);
    }

    private static async Task<string> WhoAmI(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<WhoAmIResponse>(
            new Uri("/dev/whoami", UriKind.Relative), TestContext.Current.CancellationToken);
        return response!.CustomerId;
    }

    private sealed record WhoAmIResponse([property: JsonPropertyName("customer_id")] string CustomerId);
}
