using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Billing.Domain;
using Support.Api.Modules.Identity.Application;

namespace Support.Api.Modules.Billing.Application.GetPaymentHistory;

internal sealed class GetPaymentHistoryQueryHandler(IBillingRepository billing, IPrincipalSource principals)
    : IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<Payment>>
{
    public Task<Result<IReadOnlyList<Payment>>> Handle(GetPaymentHistoryQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<Payment> payments = [.. billing.PaymentsFor(principals.Current.CustomerId).OrderByDescending(payment => payment.PaidAt)];
        return Task.FromResult(Result.Success(payments));
    }
}
