namespace Support.Api.Common.Domain;

/// <summary>
/// Business rules from fixtures/data/policies.json, shared with the Python backend. Eligibility windows are measured
/// from <see cref="ReferenceTime"/> (the fixtures' fixed "now"), so results are deterministic; approval expiry uses
/// the real clock.
/// </summary>
public sealed record SupportPolicy(
    DateTimeOffset ReferenceTime,
    IReadOnlyList<string> CancellableStatuses,
    string RefundRequiredStatus,
    int RefundWindowDays,
    IReadOnlyList<string> RefundableRefundStatuses,
    IReadOnlyList<string> TicketPriorities,
    int ApprovalExpiryMinutes);
