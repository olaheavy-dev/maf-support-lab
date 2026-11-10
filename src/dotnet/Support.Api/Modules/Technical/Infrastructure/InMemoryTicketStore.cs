using Support.Api.Modules.Technical.Application;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Infrastructure;

internal sealed class InMemoryTicketStore : ITicketStore
{
    private readonly List<SupportTicket> tickets = [];
    private readonly Lock gate = new();
    private int next = 1;

    public SupportTicket Create(string customerId, string subject, string description, string priority, string? relatedOrderId, DateTimeOffset at)
    {
        lock (gate)
        {
            var ticket = new SupportTicket(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TKT-{next++:0000}"), customerId, subject, description, priority, relatedOrderId, at);
            tickets.Add(ticket);
            return ticket;
        }
    }

    public IReadOnlyList<SupportTicket> OwnedBy(string customerId)
    {
        lock (gate)
        {
            return [.. tickets.Where(ticket => ticket.CustomerId == customerId)];
        }
    }
}
