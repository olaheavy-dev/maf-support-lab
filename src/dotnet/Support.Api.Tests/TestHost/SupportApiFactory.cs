using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Hermetic test host: fake model mode, no provider key from the developer's environment, and
/// optionally a different principal injected through DI (the trusted test-host injection point).
/// </summary>
internal static class SupportApiFactory
{
    public static WebApplicationFactory<Program> Create(
        IReadOnlyDictionary<string, string?>? settings = null,
        string? principal = null,
        string environment = "Development",
        IPrincipalSource? principalSource = null,
        IChatClient? chatClient = null)
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

            builder.ConfigureTestServices(services =>
            {
                // The in-memory test server has no client address. Present requests as a local caller,
                // like a developer's browser, so DevUI's loopback-only guard behaves as it does locally.
                // Registered first so it runs before DevUI's own access-control middleware.
                services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new LoopbackCallerStartupFilter()));

                if (principalSource is not null || principal is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(principalSource ?? new FixedPrincipalSource(new SupportPrincipal(principal!))));
                }

                if (chatClient is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(chatClient));
                }
            });
        });
    }

    private sealed class LoopbackCallerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class FixedPrincipalSource(SupportPrincipal principal) : IPrincipalSource
    {
        public SupportPrincipal Current { get; } = principal;
    }
}
