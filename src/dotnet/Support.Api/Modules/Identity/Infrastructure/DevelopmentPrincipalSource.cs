using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Identity.Infrastructure;

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
