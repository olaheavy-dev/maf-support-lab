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

    /// <summary>A fake model whose specialist calls are counted. Triage (JSON-format calls) is not counted; it can be scripted separately.</summary>
    public FakeChatClient Client(Func<string, string>? triage = null) => new((messages, options) =>
    {
        Interlocked.Increment(ref calls);
        return inner is null ? FakeChatClient.DefaultScript(messages, options) : inner(messages);
    }, triage);
}

/// <summary>Fake model that holds its first specialist call until the test releases it (triage is not held).</summary>
internal sealed class GatedModel
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;

    public GatedModel() => Client = new FakeChatClient(messages =>
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            started.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(10));
        }

        return FakeChatClient.DefaultScript(messages);
    });

    public IChatClient Client { get; }

    public int Calls => calls;

    public Task FirstCallStarted => started.Task;

    public void Release() => release.TrySetResult();
}
