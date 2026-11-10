using Support.Api.Common.Domain;

namespace Support.Api.Modules.Billing.Application;

public static class BillingErrors
{
    public static readonly Error InvalidReference = Error.Validation("invalid_reference", "Use an invoice id like INV-1234 or an order id like ORD-1234.");

    /// <summary>Used for missing invoices and for other customers' invoices alike, so callers can't learn that one exists.</summary>
    public static readonly Error NotFound = Error.NotFound("not_found", "The invoice does not exist or is not available to you.");
}
