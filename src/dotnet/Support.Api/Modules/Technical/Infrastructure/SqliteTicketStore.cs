using System.Text.Json;
using Support.Api.Common.Infrastructure.Persistence;
using Support.Api.Modules.Technical.Application;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Infrastructure;

/// <summary>Simulated tickets in the local database, numbered TKT-0001 upwards by a stored counter (L11).</summary>
internal sealed class SqliteTicketStore(SupportDatabase database) : ITicketStore
{
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    public SupportTicket Create(string customerId, string subject, string description, string priority, string? relatedOrderId, DateTimeOffset at)
    {
        var ticket = new SupportTicket(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TKT-{database.NextValue("ticket", 1):0000}"), customerId, subject, description, priority, relatedOrderId, at);
        database.Execute(
            "INSERT INTO tickets(ticket_id, customer_id, json) VALUES (@id, @customer, @json)",
            ("@id", ticket.TicketId), ("@customer", ticket.CustomerId), ("@json", JsonSerializer.Serialize(ticket, StoredJson)));
        return ticket;
    }

    public IReadOnlyList<SupportTicket> OwnedBy(string customerId) =>
        database.Query(
            "SELECT json FROM tickets WHERE customer_id = @customer ORDER BY ticket_id",
            reader => JsonSerializer.Deserialize<SupportTicket>(reader.GetString(0), StoredJson)!,
            ("@customer", customerId));
}
