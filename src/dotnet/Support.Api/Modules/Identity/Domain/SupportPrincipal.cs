using System.Text.RegularExpressions;

namespace Support.Api.Modules.Identity.Domain;

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
