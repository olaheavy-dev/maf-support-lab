using Microsoft.Extensions.AI;
using Support.Api.Modules.Identity.Application;
using Support.Api.Modules.Identity.Domain;
using Support.Api.Modules.Agents.Infrastructure.ModelProvider;

namespace Support.Api.Tests.TestHost;

/// <summary>A principal source the test can switch between customers. Lives only in the test host.</summary>
internal sealed class SwitchablePrincipalSource(string customerId) : IPrincipalSource
{
    private SupportPrincipal current = new(customerId);

    public SupportPrincipal Current => current;

    public void SwitchTo(string customerId) => current = new SupportPrincipal(customerId);
}

/// <summary>Counts model calls so tests can prove a replay did not run the model again.</summary>
internal sealed class CountingScript(Func<IReadOnlyList<ChatMessage>, ChatResponse>? inner = null)
{
    private int calls;

    public int Calls => calls;

    public FakeChatClient Client() => new(messages =>
    {
        Interlocked.Increment(ref calls);
        return (inner ?? FakeChatClient.DefaultScript)(messages);
    });
}
