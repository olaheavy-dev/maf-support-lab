using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Application.GetPaymentHistory;

/// <summary>The current customer's charges and refunds, newest first. It takes no input: the customer comes from the server.</summary>
public sealed record GetPaymentHistoryQuery : IQuery<IReadOnlyList<Payment>>;
