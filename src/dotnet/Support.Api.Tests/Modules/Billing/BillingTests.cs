using Microsoft.Extensions.DependencyInjection;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;
using Support.Api.Modules.Billing.Presentation;

namespace Support.Api.Tests.Modules.Billing;

/// <summary>Billing reads are scoped to the server's principal, whatever the model asks for.</summary>
public sealed class BillingTests
{
    [Fact]
    public async Task An_invoice_can_be_found_by_its_own_id()
    {
        var turn = await AskAsync("Please send invoice INV-5002 again.");

        Assert.Equal("billing", turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("answer", turn.ReplyKind());
        Assert.Contains("INV-5002", turn.ReplyText(), StringComparison.Ordinal);
        Assert.Contains("ORD-1002", turn.ReplyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_customers_invoice_is_not_found_even_when_the_model_asks_for_it()
    {
        // A model that always requests CUST-1002's invoice, whatever the customer said.
        var model = new CountingScript(messages => messages[^1].Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any()
            ? FakeChatClient.DefaultScript(messages, null)
            : FakeChatClient.ToolCall(FakeChatClient.InvoiceToolName, new Dictionary<string, object?> { ["reference"] = "INV-5004" }));

        var turn = await AskAsync("Can I see my invoice for ORD-1001?", model);

        Assert.Equal("not_found", turn.ReplyKind());
        Assert.DoesNotContain("ORD-2001", turn.ReplyText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_results_never_contain_another_customers_billing()
    {
        using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var tools = factory.Services.GetRequiredService<BillingTools>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var foreign = await tools.GetInvoiceAsync("INV-5004", cancellationToken);
        var unknown = await tools.GetInvoiceAsync("INV-9999", cancellationToken);
        var foreignByOrder = await tools.GetInvoiceAsync("ORD-2001", cancellationToken);
        var history = await tools.GetPaymentHistoryAsync(cancellationToken);

        Assert.Equal(unknown with { Reference = "INV-5004" }, foreign);
        Assert.Equal("not_found", foreignByOrder.Lookup);
        Assert.Equal(["PAY-7003", "PAY-7002", "PAY-7001"], history.Payments.Select(payment => payment.PaymentId));
        Assert.Equal("invalid_reference", (await tools.GetInvoiceAsync("my last one", cancellationToken)).Lookup);
    }

    [Fact]
    public async Task An_invoice_question_without_an_id_asks_which_one()
    {
        var turn = await AskAsync("Can you send me a copy of my invoice?");

        Assert.Equal("billing", turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("clarification", turn.ReplyKind());
    }

    private static async Task<System.Text.Json.JsonElement> AskAsync(string message, CountingScript? model = null)
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001", chatClient: (model ?? new CountingScript()).Client());
        var api = new ApiClient(factory.CreateClient());
        return await api.AskAsync(await api.CreateConversationAsync(), message);
    }
}
