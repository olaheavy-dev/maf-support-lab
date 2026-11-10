namespace Support.Api.Modules.Technical.Domain;

/// <summary>A simulated support ticket (stored in the local database, never sent anywhere).</summary>
public sealed record SupportTicket(string TicketId, string CustomerId, string Subject, string Description, string Priority, string? RelatedOrderId, DateTimeOffset CreatedAt);
