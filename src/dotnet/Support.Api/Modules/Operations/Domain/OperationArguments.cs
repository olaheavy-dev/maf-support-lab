using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Api.Modules.Operations.Domain;

/// <summary>
/// The exact arguments of a write, in contract form (snake_case, e.g. {"order_id":"ORD-1003"}). Two argument sets are
/// equal only if their canonical JSON (keys sorted) is identical, so an approval can never be reused for an action
/// with any argument changed.
/// </summary>
public sealed class OperationArguments : IEquatable<OperationArguments>
{
    private readonly SortedDictionary<string, string> values;

    public OperationArguments(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.values = new SortedDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        Canonical = JsonSerializer.Serialize(this.values);
    }

    public string Canonical { get; }

    public IReadOnlyDictionary<string, string> Values => values;

    public string? this[string name] => values.GetValueOrDefault(name);

    public JsonObject ToJson() => new(values.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)));

    public bool Equals(OperationArguments? other) => other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as OperationArguments);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);
}
