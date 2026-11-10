using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Application;

/// <summary>Where simulated tickets are kept: durably (L11), issuing TKT-… ids.</summary>
public interface ITicketStore
{
    SupportTicket Create(string customerId, string subject, string description, string priority, string? relatedOrderId, DateTimeOffset at);

    IReadOnlyList<SupportTicket> OwnedBy(string customerId);
}
