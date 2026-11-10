using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Billing.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Billing.Application.GetInvoice;

/// <summary>Invoice reads with ownership enforced in code, from the trusted principal only.</summary>
internal sealed class GetInvoiceQueryHandler(IBillingRepository billing, IPrincipalSource principals)
    : IQueryHandler<GetInvoiceQuery, Invoice>
{
    public Task<Result<Invoice>> Handle(GetInvoiceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var reference = InvoiceReference.Normalize(query.Reference);
        var invoice = InvoiceReference.IsInvoiceId(reference) ? billing.FindInvoice(reference)
            : InvoiceReference.IsOrderId(reference) ? billing.FindInvoiceForOrder(reference)
            : null;

        if (invoice is null && !InvoiceReference.IsInvoiceId(reference) && !InvoiceReference.IsOrderId(reference))
        {
            return Task.FromResult(Result.Failure<Invoice>(BillingErrors.InvalidReference));
        }

        // Another customer's invoice is reported exactly like a missing one.
        var owned = invoice is not null && string.Equals(invoice.CustomerId, principals.Current.CustomerId, StringComparison.Ordinal);
        return Task.FromResult(owned ? Result.Success(invoice!) : Result.Failure<Invoice>(BillingErrors.NotFound));
    }
}
