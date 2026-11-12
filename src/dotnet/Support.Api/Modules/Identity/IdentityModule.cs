using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Identity.Infrastructure;
using Support.Api.Modules.Identity.Presentation;

namespace Support.Api.Modules.Identity;

/// <summary>Wires the Identity module: who the current customer is, and nothing a client can influence.</summary>
internal static class IdentityModule
{
    /// <summary>
    /// The configured principal source (development customer, or a test's principal). The application resolves
    /// <see cref="IPrincipalSource"/>, which wraps it so a detached turn's pinned principal wins.
    /// </summary>
    public const string ConfiguredSourceKey = "configured";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IPrincipalSource>(provider => new PinnedPrincipalSource(provider.GetRequiredKeyedService<IPrincipalSource>(ConfiguredSourceKey)));

        // Fail closed: outside Development there is no identity provider until production authentication exists.
        if (!environment.IsDevelopment())
        {
            services.AddKeyedSingleton<IPrincipalSource>(ConfiguredSourceKey, (_, _) => throw new InvalidOperationException(
                "No identity provider is configured for this environment. The development principal is only available in Development."));
            return services;
        }

        services.AddKeyedSingleton<IPrincipalSource>(ConfiguredSourceKey, (_, _) =>
        {
            var customerId = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>()?.DevelopmentCustomerId;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                throw new InvalidOperationException("Identity:DevelopmentCustomerId must be set in Development.");
            }

            return new DevelopmentPrincipalSource(new SupportPrincipal(customerId));
        });

        return services;
    }

    public static WebApplication MapIdentityModule(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapWhoAmI();
        }

        return app;
    }
}
