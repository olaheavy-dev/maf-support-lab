using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Support.Api.Modules.Billing.Application;
using Support.Api.Modules.Operations.Application;
using Support.Api.Modules.Operations.Domain;
using Support.Api.Modules.Orders.Application;
using Support.Api.Modules.Technical.Application;

namespace Support.Api.Tests.Scenarios;

/// <summary>
/// Runs the shared feature scenarios (fixtures/scenarios/feature-scenarios.json) through the version-1 API in fake
/// mode. The same file will drive the Python backend, so both implementations are held to one description of
/// behaviour. Every scenario in the file is run; steps send a message, resend the previous one, or decide the pending
/// approval (optionally as another customer), and the scenario's expected state change is checked at the end.
/// </summary>
public sealed class FeatureScenarioTests
{
    private static readonly JsonElement Scenarios = LoadScenarios();

    public static TheoryData<string> All() =>
        [.. Scenarios.GetProperty("scenarios").EnumerateArray().Select(scenario => scenario.GetProperty("scenario_id").GetString()!)];

    [Theory]
    [MemberData(nameof(All))]
    public async Task Scenario_behaves_as_described(string scenarioId)
    {
        var scenario = Scenarios.GetProperty("scenarios").EnumerateArray().Single(item => item.GetProperty("scenario_id").GetString() == scenarioId);
        var owner = scenario.GetProperty("principal").GetString()!;
        var principals = new SwitchablePrincipalSource(owner);
        await using var factory = SupportApiFactory.Create(principalSource: principals);
        var api = new ApiClient(factory.CreateClient());
        var conversation = await api.CreateConversationAsync();

        JsonElement? pendingApproval = null;
        string? lastTurnId = null;
        string? lastOperationId = null;
        object? lastRequest = null;

        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            var expect = step.GetProperty("expect");
            principals.SwitchTo(step.TryGetProperty("as_principal", out var asPrincipal) ? asPrincipal.GetString()! : owner);
            var action = step.GetProperty("action").GetString();

            (HttpStatusCode Status, JsonElement Body) response;
            if (action == "decide")
            {
                var pending = pendingApproval ?? throw new InvalidOperationException($"{scenarioId}: no pending approval to decide.");
                response = await api.DecideAsync(conversation, pending.GetProperty("approval_id").GetString()!, pending.GetProperty("operation_id").GetString()!, step.GetProperty("decision").GetString()!);
            }
            else
            {
                lastRequest = action == "resend_previous" ? lastRequest : new { client_turn_id = Guid.NewGuid().ToString(), message = step.GetProperty("message").GetString() };
                response = await api.SendTurnAsync(conversation, lastRequest!);
                if (response.Status == HttpStatusCode.OK)
                {
                    ContractSchemas.AssertValid("turn-result", response.Body);
                }
            }

            principals.SwitchTo(owner);
            var (status, body) = response;
            if (expect.TryGetProperty("http_status", out var httpStatus))
            {
                Assert.Equal(httpStatus.GetInt32(), (int)status);
                Assert.Equal(expect.GetProperty("problem_code").GetString(), body.GetProperty("code").GetString());
                continue;
            }

            Assert.Equal(HttpStatusCode.OK, status);
            AssertTurn(body, expect);
            if (expect.TryGetProperty("same_turn_as_previous", out _))
            {
                Assert.Equal(lastTurnId, body.GetProperty("turn_id").GetString());
            }

            var operationId = body.TryGetProperty("pending_approval", out var pending2) ? pending2.GetProperty("operation_id").GetString()
                : body.GetProperty("operations").EnumerateArray().Select(op => op.GetProperty("operation_id").GetString()).FirstOrDefault();
            if (expect.TryGetProperty("operation_kind", out var operationKind))
            {
                Assert.Equal(operationKind.GetString(), body.GetProperty("pending_approval").GetProperty("kind").GetString());
            }

            if (expect.TryGetProperty("operation_status", out var operationStatus))
            {
                Assert.Equal(operationStatus.GetString(), (await api.GetOperationAsync(operationId!)).GetProperty("status").GetString());
            }

            if (expect.TryGetProperty("same_operation_as_previous", out _))
            {
                Assert.Equal(lastOperationId, operationId);
            }

            pendingApproval = body.TryGetProperty("pending_approval", out var nextPending) ? nextPending.Clone() : pendingApproval;
            lastTurnId = body.GetProperty("turn_id").GetString();
            lastOperationId = operationId ?? lastOperationId;
        }

        AssertStateChange(factory, scenario.GetProperty("expected_state_change"), owner, conversation);
    }

    private static void AssertTurn(JsonElement turn, JsonElement expect)
    {
        var route = turn.GetProperty("route");
        if (expect.TryGetProperty("route_status", out var status))
        {
            Assert.Equal(status.GetString(), route.GetProperty("status").GetString());
        }

        if (expect.TryGetProperty("route_target", out var target))
        {
            Assert.Equal(target.GetString(), route.GetProperty("target").GetString());
        }

        if (expect.TryGetProperty("outcome", out var outcome))
        {
            Assert.Equal(outcome.GetString(), turn.GetProperty("outcome").GetString());
        }

        if (expect.TryGetProperty("reply_kind", out var replyKind))
        {
            Assert.Equal(replyKind.GetString(), turn.ReplyKind());
        }

        foreach (var mention in Strings(expect, "mentions"))
        {
            Assert.Contains(mention, turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var forbidden in Strings(expect, "must_not_mention"))
        {
            Assert.DoesNotContain(forbidden, turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Checks the simulated business state the scenario says should (or should not) have changed.</summary>
    private static void AssertStateChange(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, JsonElement change, string owner, string conversation)
    {
        var committed = factory.Services.GetRequiredService<IOperationStore>().ForConversation(conversation).Count(op => op.Status == OperationStatus.Committed);
        var orders = factory.Services.GetRequiredService<IOrderRepository>();
        switch (change.GetProperty("type").GetString())
        {
            case "none":
                Assert.Equal(0, committed);
                break;
            case "order_cancelled":
                Assert.Equal("cancelled", orders.Find(change.GetProperty("order_id").GetString()!)!.Status);
                Assert.Equal(1, committed);
                break;
            case "refund_recorded":
                var orderId = change.GetProperty("order_id").GetString()!;
                Assert.Single(factory.Services.GetRequiredService<IBillingRepository>().PaymentsFor(owner), payment => payment.Kind == "refund" && payment.OrderId == orderId && payment.PaymentId.StartsWith("PAY-8", StringComparison.Ordinal));
                Assert.Equal("refunded", orders.Find(orderId)!.RefundStatus);
                break;
            case "ticket_created":
                Assert.Single(factory.Services.GetRequiredService<ITicketStore>().OwnedBy(owner));
                break;
            default:
                throw new InvalidOperationException("Unknown expected_state_change type.");
        }
    }

    private static JsonElement LoadScenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSchemas.RepositoryRoot(), "fixtures", "scenarios", "feature-scenarios.json")));
        return document.RootElement.Clone();
    }

    private static IEnumerable<string> Strings(JsonElement expect, string name) =>
        expect.TryGetProperty(name, out var values) ? values.EnumerateArray().Select(value => value.GetString()!) : [];
}
