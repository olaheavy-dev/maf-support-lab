using System.Text;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Quality;

/// <summary>
/// Turns a case's context references (orders:ORD-1002, policies:refund, ...) into the exact fixture records the
/// groundedness judge reads. The context is the evidence a correct reply could use, never the expected answer, so the
/// judge measures "supported by the records" rather than "matches a gold reply". An unknown reference is an error:
/// judging against incomplete context would make an ungrounded score look like the reply's fault.
/// </summary>
internal sealed class GroundingContext(JsonNode orders, JsonNode invoices, JsonNode payments, JsonNode articles, JsonNode policies)
{
    public static GroundingContext Load(string? fixturesDirectory = null)
    {
        var directory = fixturesDirectory ?? Path.Combine(RepositoryRoot.Find(), "fixtures", "data");
        JsonNode Read(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, file)))!;
        return new GroundingContext(Read("orders.json")["orders"]!, Read("invoices.json")["invoices"]!, Read("payments.json")["payments"]!, Read("knowledge-base.json")["articles"]!, Read("policies.json"));
    }

    /// <summary>One line per record ("orders:ORD-1002 = {...}"), in reference order.</summary>
    public string Resolve(IReadOnlyList<string>? references)
    {
        if (references is null || references.Count == 0)
        {
            throw new InvalidDataException("no grounding context: the case lists no fixture references");
        }

        StringBuilder text = new();
        foreach (var reference in references)
        {
            text.Append(reference).Append(" = ").AppendLine(Record(reference).ToJsonString());
        }

        // Policies are judged "as of" the fixture clock (for example a refund window), so the clock is part of the context.
        if (references.Any(reference => reference.StartsWith("policies:", StringComparison.Ordinal)))
        {
            text.Append("policies:reference_time = ").AppendLine(policies["reference_time"]!.ToJsonString());
        }

        return text.ToString().TrimEnd();
    }

    private JsonNode Record(string reference)
    {
        var parts = reference.Split(':', 2);
        var (kind, id) = parts.Length == 2 ? (parts[0], parts[1]) : (reference, "");
        var found = kind switch
        {
            "orders" => Find(orders, "order_id", id),
            "invoices" => Find(invoices, "invoice_id", id),
            "payments" => Find(payments, "payment_id", id),
            "knowledge-base" => Find(articles, "article_id", id),
            "policies" => policies[id] is JsonObject section ? section : null,
            _ => null,
        };
        return found?.DeepClone() ?? throw new InvalidDataException($"unknown fixture reference '{reference}'");
    }

    private static JsonNode? Find(JsonNode list, string key, string id) =>
        list.AsArray().FirstOrDefault(item => string.Equals(item?[key]?.GetValue<string>(), id, StringComparison.Ordinal));
}
