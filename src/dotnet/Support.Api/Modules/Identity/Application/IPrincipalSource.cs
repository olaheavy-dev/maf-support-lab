using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Identity.Application;

/// <summary>
/// The single source of the current principal. The application asks this service and nothing else:
/// headers, query strings, request bodies, and model output are never consulted for identity.
/// Tests replace this service through the test host (a trusted injection point the client cannot reach).
/// </summary>
public interface IPrincipalSource
{
    SupportPrincipal Current { get; }
}
