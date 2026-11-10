using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Tests.TestHost;

/// <summary>
/// Hermetic test host: fake model mode, no provider key from the developer's environment, its own temporary database
/// (deleted when the host is disposed) unless the test passes <c>Storage:Path</c> to share one across a "restart", and
/// optionally a different principal injected through DI (the trusted test-host injection point).
/// </summary>
internal static class SupportApiFactory
{
    public static WebApplicationFactory<Program> Create(
        IReadOnlyDictionary<string, string?>? settings = null,
        string? principal = null,
        string environment = "Development",
        IPrincipalSource? principalSource = null,
        IChatClient? chatClient = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var ownDirectory = settings?.ContainsKey("Storage:Path") == true ? null : TestDatabase.NewDirectory();
        return new IsolatedHost(ownDirectory, builder =>
        {
            builder.UseEnvironment(environment);
            if (ownDirectory is not null)
            {
                builder.UseSetting("Storage:Path", Path.Combine(ownDirectory, "support.db"));
            }


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

                configureServices?.Invoke(services);

                if (chatClient is not null)
                {
                    // Swap the raw provider client, so the scripted model still runs through the telemetry wrapper.
                    services.Replace(ServiceDescriptor.KeyedSingleton(ModelProviderRegistration.ProviderClientKey, chatClient));
                }
            });
        });
    }

    /// <summary>A test host that deletes its own temporary database directory when disposed.</summary>
    private sealed class IsolatedHost(string? ownDirectory, Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            TestDatabase.Delete(ownDirectory);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TestDatabase.Delete(ownDirectory);
        }
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
