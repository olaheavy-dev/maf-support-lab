using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Billing.Domain;

namespace Support.Api.Modules.Billing.Application.GetInvoice;

/// <summary>Read one of the current customer's invoices by invoice or order id. There is deliberately no customer id here.</summary>
public sealed record GetInvoiceQuery(string Reference) : IQuery<Invoice>;
