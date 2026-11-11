using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Support.Evaluations.Native;

/// <summary>
/// The agent native evaluation runs (L30): a MAF <see cref="ChatClientAgent"/> over a scripted chat client, with tools
/// that have the .NET backend's names and parameters and read the shared fixtures for one customer. Reads check
/// ownership; writes are approval-required and never run here. No credentials, no network.
/// </summary>
/// <remarks>
/// It stands in for the support agent's model, so MAF's evaluation orchestration can be exercised end to end. It is not
/// the backend: the backend's business gates are evaluated through its API (L16).
/// </remarks>
internal sealed partial class FakeSupportAgent
{
    public const string Name = "support-fake-agent";
    public static readonly string[] WriteTools = ["cancel_order", "refund_order", "create_ticket"];

    private readonly Fixtures fixtures;
    private readonly string customer;

    public FakeSupportAgent(string customer, Fixtures? fixtures = null)
    {
        this.customer = customer;
        this.fixtures = fixtures ?? Fixtures.Load();
        Model = new ScriptedModel();
        Tools =
        [
            AIFunctionFactory.Create(GetOrderStatus, "get_order_status"),
            AIFunctionFactory.Create(GetInvoice, "get_invoice"),
            AIFunctionFactory.Create(GetPaymentHistory, "get_payment_history"),
            AIFunctionFactory.Create(SearchKnowledgeBase, "search_knowledge_base"),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CancelOrder, "cancel_order")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(RefundOrder, "refund_order")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateTicket, "create_ticket")),
        ];
        Agent = new ChatClientAgent(Model, new ChatClientAgentOptions
        {
            Name = Name,
            ChatOptions = new ChatOptions { Instructions = "You are VoltCart's support agent.", Tools = [.. Tools] },
        });
    }

    /// <summary>The model, so a test can see whether a call was made (recorded evaluation must make none).</summary>
    public ScriptedModel Model { get; }

    public IReadOnlyList<AITool> Tools { get; }

    public AIAgent Agent { get; }

    [Description("Look up the status of one of the current customer's orders.")]
    private string GetOrderStatus([Description("The order id, in the form ORD-1234.")] string orderId)
    {
        var order = fixtures.Order(orderId);
        return order is not null && Owner(order) == customer
            ? Json(new { lookup = "found", order_id = orderId, order_status = order["status"]!.GetValue<string>() })
            : Json(new { lookup = "not_found", order_id = orderId });
    }

    [Description("Look up one of the current customer's invoices, by invoice id (INV-1234) or by order id (ORD-1234).")]
    private string GetInvoice([Description("The invoice id (INV-1234) or order id (ORD-1234).")] string reference)
    {
        var invoice = fixtures.Invoice(reference);
        return invoice is not null && Owner(fixtures.Order(invoice["order_id"]!.GetValue<string>())!) == customer
            ? Json(new { lookup = "found", invoice_id = invoice["invoice_id"]!.GetValue<string>(), order_id = invoice["order_id"]!.GetValue<string>() })
            : Json(new { lookup = "not_found", reference });
    }

    [Description("List the current customer's payments and refunds.")]
    private string GetPaymentHistory() =>
        Json(new { payments = fixtures.Payments(customer).Select(p => new { payment_id = p["payment_id"]!.GetValue<string>(), order_id = p["order_id"]!.GetValue<string>(), kind = p["kind"]!.GetValue<string>() }) });

    [Description("Search the troubleshooting articles for a product problem.")]
    private string SearchKnowledgeBase([Description("Product and symptom keywords.")] string query) =>
        Json(new { articles = fixtures.Search(query).Select(a => new { article_id = a["article_id"]!.GetValue<string>(), title = a["title"]!.GetValue<string>() }) });

    // Writes pause for approval: MAF returns an approval request instead of calling these.
    [Description("Cancel one of the current customer's orders. Requires the customer's approval.")]
    private static string CancelOrder([Description("The order id, in the form ORD-1234.")] string orderId) => throw new InvalidOperationException($"A write ran without approval: cancel {orderId}.");

    [Description("Refund one of the current customer's delivered orders in full. Requires the customer's approval.")]
    private static string RefundOrder([Description("The order id, in the form ORD-1234.")] string orderId, [Description("The customer's reason.")] string reason) => throw new InvalidOperationException($"A write ran without approval: refund {orderId} ({reason.Length} chars).");

    [Description("Open a support ticket. Requires the customer's approval.")]
    private static string CreateTicket([Description("A short subject line.")] string subject, [Description("What is wrong.")] string description) => throw new InvalidOperationException($"A write ran without approval: ticket {subject} ({description.Length} chars).");

    private static string? Owner(JsonNode order) => Fixtures.OwnerOf(order);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A deterministic stand-in for the model, like the backends' fakes: it asks for the fitting tool (arguments taken
    /// from the message), then answers from the tool's result, citing the record it found. It counts every call.
    /// </summary>
    internal sealed partial class ScriptedModel : IChatClient
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        /// <summary>Optional override for tests: the tool call to make instead of the scripted one (for example a wrong argument).</summary>
        public Func<string, FunctionCallContent?>? Override { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            IReadOnlyList<ChatMessage> history = [.. messages];
            var result = history[^1].Contents.OfType<FunctionResultContent>().LastOrDefault();
            if (result is not null)
            {
                return Task.FromResult(Reply(Describe(result.Result?.ToString() ?? "")));
            }

            var text = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
            var call = Override is { } custom ? custom(text) : Decide(text);
            return Task.FromResult(call is null
                ? Reply("Which order do you mean? Please share the order id, for example ORD-1234.")
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        internal static FunctionCallContent? Decide(string text)
        {
            var upper = text.ToUpperInvariant();
            var order = OrderId().Match(text);
            var invoice = InvoiceId().Match(text);
            FunctionCallContent Call(string name, Dictionary<string, object?> arguments) => new($"call_{name}", name, arguments); // one call per turn: a stable id keeps recordings diffable
            if (upper.Contains("CANCEL", StringComparison.Ordinal) && order.Success)
            {
                return Call("cancel_order", new() { ["orderId"] = order.Value.ToUpperInvariant() });
            }

            if (upper.Contains("REFUND", StringComparison.Ordinal) && order.Success)
            {
                return Call("refund_order", new() { ["orderId"] = order.Value.ToUpperInvariant(), ["reason"] = text });
            }

            if (upper.Contains("TICKET", StringComparison.Ordinal))
            {
                return Call("create_ticket", new() { ["subject"] = "Support request", ["description"] = text });
            }

            if (upper.Contains("INVOICE", StringComparison.Ordinal) && (invoice.Success || order.Success))
            {
                return Call("get_invoice", new() { ["reference"] = (invoice.Success ? invoice.Value : order.Value).ToUpperInvariant() });
            }

            if (upper.Contains("PAYMENT", StringComparison.Ordinal))
            {
                return Call("get_payment_history", []);
            }

            if (order.Success)
            {
                return Call("get_order_status", new() { ["orderId"] = order.Value.ToUpperInvariant() });
            }

            return upper.Contains("CRACKL", StringComparison.Ordinal) || upper.Contains("ERROR", StringComparison.Ordinal) || upper.Contains("BROKEN", StringComparison.Ordinal)
                ? Call("search_knowledge_base", new() { ["query"] = text })
                : null;
        }

        private static string Describe(string result)
        {
            var data = JsonNode.Parse(result);
            if (data?["lookup"]?.GetValue<string>() == "not_found")
            {
                return "I couldn't find that on your account. Please check the id.";
            }

            if (data?["order_status"] is { } status)
            {
                return $"Order {data["order_id"]} is {status}.";
            }

            if (data?["invoice_id"] is { } invoiceId)
            {
                return $"Invoice {invoiceId} is for order {data["order_id"]}.";
            }

            if (data?["payments"] is JsonArray payments)
            {
                return "Your payments: " + string.Join("; ", payments.Select(p => $"{p!["payment_id"]} {p["kind"]} for {p["order_id"]}")) + ".";
            }

            if (data?["articles"] is JsonArray articles)
            {
                return articles.Count == 0 ? "I couldn't find a documented fix. I can pass this to a human." : $"See {articles[0]!["article_id"]} ({articles[0]!["title"]}).";
            }

            return "Done.";
        }

        private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

        [GeneratedRegex("ORD-[0-9]{4}", RegexOptions.IgnoreCase)]
        private static partial Regex OrderId();

        [GeneratedRegex("INV-[0-9]{4}", RegexOptions.IgnoreCase)]
        private static partial Regex InvoiceId();
    }
}

/// <summary>The shared synthetic fixtures (fixtures/data), read-only.</summary>
internal sealed class Fixtures(JsonNode orders, JsonNode invoices, JsonNode payments, JsonNode articles)
{
    public static Fixtures Load()
    {
        var directory = Path.Combine(RepositoryRoot.Find(), "fixtures", "data");
        JsonNode Read(string file, string key) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, file)))![key]!;
        return new Fixtures(Read("orders.json", "orders"), Read("invoices.json", "invoices"), Read("payments.json", "payments"), Read("knowledge-base.json", "articles"));
    }

    public JsonNode? Order(string id) => orders.AsArray().FirstOrDefault(o => string.Equals(o!["order_id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

    public static string? OwnerOf(JsonNode order) => order["customer_id"]?.GetValue<string>();

    public JsonNode? Invoice(string reference) => invoices.AsArray().FirstOrDefault(i =>
        string.Equals(i!["invoice_id"]!.GetValue<string>(), reference, StringComparison.OrdinalIgnoreCase)
        || string.Equals(i["order_id"]!.GetValue<string>(), reference, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<JsonNode> Payments(string customer) => payments.AsArray().OfType<JsonNode>().Where(p =>
        Order(p["order_id"]!.GetValue<string>()) is { } order && OwnerOf(order) == customer);

    public IEnumerable<JsonNode> Search(string query)
    {
        var words = query.Split([' ', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        return articles.AsArray().OfType<JsonNode>().Where(a => a["keywords"]!.AsArray().Any(k => words.Any(w => w.StartsWith(k!.GetValue<string>().ToUpperInvariant(), StringComparison.Ordinal))));
    }
}
