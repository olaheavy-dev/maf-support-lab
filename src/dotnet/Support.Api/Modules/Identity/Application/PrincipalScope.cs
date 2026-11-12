using Support.Api.Modules.Identity.Domain;

namespace Support.Api.Modules.Identity.Application;

/// <summary>
/// Pins the principal for work that outlives its HTTP request. A turn runs detached from the request
/// that started it; the caller's principal is captured when the turn is accepted and pinned here for the whole run, so
/// identity can never change mid-turn or depend on a request that has already ended.
/// </summary>
public static class PrincipalScope
{
    private static readonly AsyncLocal<SupportPrincipal?> Pinned = new();

    /// <summary>The pinned principal, if this code runs inside a scope.</summary>
    public static SupportPrincipal? Current => Pinned.Value;

    public static IDisposable Begin(SupportPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var previous = Pinned.Value;
        Pinned.Value = principal;
        return new Restore(previous);
    }

    private sealed class Restore(SupportPrincipal? previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }
}

/// <summary>
/// The application's <see cref="IPrincipalSource"/>: a pinned principal (inside a detached turn) wins, otherwise the
/// configured source decides. Nothing here reads the request: identity comes only from trusted server-side sources.
/// </summary>
internal sealed class PinnedPrincipalSource(IPrincipalSource configured) : IPrincipalSource
{
    public SupportPrincipal Current => PrincipalScope.Current ?? configured.Current;
}
