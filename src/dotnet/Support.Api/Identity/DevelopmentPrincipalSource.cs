namespace Support.Api.Identity;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>The fixed local development customer, set in server configuration.</summary>
    public string? DevelopmentCustomerId { get; set; }
}

/// <summary>Always returns the configured development customer. This is not authentication.</summary>
internal sealed class DevelopmentPrincipalSource(SupportPrincipal principal) : IPrincipalSource
{
    public SupportPrincipal Current { get; } = principal;
}

internal static class IdentityRegistration
{
    public static IServiceCollection AddDevelopmentIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Fail closed: outside Development there is no identity provider until production authentication exists.
        if (!environment.IsDevelopment())
        {
            services.AddSingleton<IPrincipalSource>(_ => throw new InvalidOperationException(
                "No identity provider is configured for this environment. The development principal is only available in Development."));
            return services;
        }

        services.AddSingleton<IPrincipalSource>(_ =>
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
}
