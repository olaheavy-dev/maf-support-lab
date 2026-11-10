using System.Text.Json;
using System.Text.RegularExpressions;
using Support.Api.Common.Domain;
using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>
/// The write tools' names and how their arguments map to contract arguments. The same mapping is used when an approval
/// is proposed (from the model's tool call) and when the approved tool runs, so "exact arguments" means exactly the
/// same canonical values on both sides.
/// </summary>
public static partial class WriteTools
{
    public const string CancelOrder = "cancel_order";
    public const string RefundOrder = "refund_order";
    public const string CreateTicket = "create_ticket";

    public static OperationKind? KindOf(string toolName) => toolName switch
    {
        CancelOrder => OperationKind.CancelOrder,
        RefundOrder => OperationKind.RefundOrder,
        CreateTicket => OperationKind.CreateTicket,
        _ => null,
    };

    /// <summary>Maps a model tool call (parameter names as the tools declare them) to validated contract arguments.</summary>
    public static Result<OperationArguments> Map(OperationKind kind, IReadOnlyDictionary<string, object?> toolArguments, IReadOnlyCollection<string> priorities)
    {
        ArgumentNullException.ThrowIfNull(toolArguments);
        string? Text(string name) => toolArguments.TryGetValue(name, out var value) ? value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement element => element.ToString(),
            _ => value.ToString(),
        } : null;

        // Free text from the model must be printable: control characters (other than newline and tab) are rejected (L14).
        if (toolArguments.Keys.Select(Text).Any(value => value?.Any(character => char.IsControl(character) && character is not ('\n' or '\t')) == true))
        {
            return Invalid("Arguments must be printable text.");
        }

        return kind switch
        {
            OperationKind.CancelOrder => Cancel(Text("orderId")),
            OperationKind.RefundOrder => Refund(Text("orderId"), Text("reason")),
            OperationKind.CreateTicket => Ticket(Text("subject"), Text("description"), Text("priority"), Text("relatedOrderId"), priorities),
            _ => Invalid("Unknown operation."),
        };
    }

    public static Result<OperationArguments> Cancel(string? orderId) =>
        NormalizeOrderId(orderId) is { } id ? Ok(("order_id", id)) : Invalid("order_id must look like ORD-1234.");

    public static Result<OperationArguments> Refund(string? orderId, string? reason)
    {
        var id = NormalizeOrderId(orderId);
        var text = reason?.Trim();
        return id is null ? Invalid("order_id must look like ORD-1234.")
            : string.IsNullOrEmpty(text) || text.Length > 500 ? Invalid("reason must be 1 to 500 characters.")
            : Ok(("order_id", id), ("reason", text));
    }

    public static Result<OperationArguments> Ticket(string? subject, string? description, string? priority, string? relatedOrderId, IReadOnlyCollection<string> priorities)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        var s = subject?.Trim();
        var d = description?.Trim();
        var p = string.IsNullOrWhiteSpace(priority) ? "normal" : priority.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s) || s.Length > 120 || string.IsNullOrEmpty(d) || d.Length > 2000)
        {
            return Invalid("subject must be 1 to 120 characters and description 1 to 2000.");
        }

        if (!priorities.Contains(p))
        {
            return Invalid("priority must be low, normal, or high.");
        }

        if (string.IsNullOrWhiteSpace(relatedOrderId))
        {
            return Ok(("subject", s), ("description", d), ("priority", p));
        }

        return NormalizeOrderId(relatedOrderId) is { } related
            ? Ok(("subject", s), ("description", d), ("priority", p), ("related_order_id", related))
            : Invalid("related_order_id must look like ORD-1234.");
    }

    private static string? NormalizeOrderId(string? orderId)
    {
        var normalized = orderId?.Trim().ToUpperInvariant() ?? string.Empty;
        return OrderIdPattern().IsMatch(normalized) ? normalized : null;
    }

    private static Result<OperationArguments> Ok(params (string Name, string Value)[] values) =>
        Result.Success(new OperationArguments(values.Select(value => KeyValuePair.Create(value.Name, value.Value))));

    private static Result<OperationArguments> Invalid(string detail) => Result.Failure<OperationArguments>(WriteErrors.InvalidArguments(detail));

    [GeneratedRegex("^ORD-[0-9]{4}$")]
    private static partial Regex OrderIdPattern();
}
