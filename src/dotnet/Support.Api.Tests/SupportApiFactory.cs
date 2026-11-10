using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Support.Api.Identity;

namespace Support.Api.Tests;

/// <summary>
/// Hermetic test host: fake model mode, no provider key from the developer's environment, and
/// optionally a different principal injected through DI (the trusted test-host injection point).
/// </summary>
internal static class SupportApiFactory
{
    public static WebApplicationFactory<Program> Create(
        IReadOnlyDictionary<string, string?>? settings = null,
        string? principal = null,
        string environment = "Development")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);

            // Blank out anything the developer machine might provide, then apply the test's settings.
            builder.UseSetting("ModelProvider:Mode", "Fake");
            builder.UseSetting("ModelProvider:Anthropic:ApiKey", string.Empty);
            builder.UseSetting("ANTHROPIC_API_KEY", string.Empty);
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            if (principal is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton<IPrincipalSource>(new FixedPrincipalSource(new SupportPrincipal(principal)))));
            }
        });
    }

    private sealed class FixedPrincipalSource(SupportPrincipal principal) : IPrincipalSource
    {
        public SupportPrincipal Current { get; } = principal;
    }
}
