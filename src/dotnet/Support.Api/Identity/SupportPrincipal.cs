using System.Text.RegularExpressions;

namespace Support.Api.Identity;

/// <summary>The customer a request runs as. Created only by trusted server code, never from request data.</summary>
public sealed partial record SupportPrincipal
{
    public SupportPrincipal(string customerId)
    {
        if (!CustomerIdPattern().IsMatch(customerId))
        {
            throw new ArgumentException("Customer id must match CUST-NNNN.", nameof(customerId));
        }

        CustomerId = customerId;
    }

    public string CustomerId { get; }

    [GeneratedRegex("^CUST-[0-9]{4}$")]
    private static partial Regex CustomerIdPattern();
}

/// <summary>
/// The single source of the current principal. The application asks this service and nothing else:
/// headers, query strings, request bodies, and model output are never consulted for identity.
/// Tests replace this service through the test host (a trusted injection point the client cannot reach).
/// </summary>
public interface IPrincipalSource
{
    SupportPrincipal Current { get; }
}
