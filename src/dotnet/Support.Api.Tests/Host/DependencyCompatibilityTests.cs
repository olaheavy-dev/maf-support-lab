using Anthropic;
using Microsoft.Agents.AI;

namespace Support.Api.Tests.Host;

/// <summary>
/// L01 risk: Anthropic C# SDK 13.x is a newer major than the 12.x floor declared by
/// Microsoft.Agents.AI.Anthropic 1.28.0. This loads both together and builds an agent without
/// calling the network. Agent behavior itself starts in L05.
/// </summary>
public sealed class DependencyCompatibilityTests
{
    [Fact]
    public void Agent_framework_Anthropic_integration_runs_with_the_selected_SDK()
    {
        var client = new AnthropicClient { ApiKey = "not-used-no-network-call" };

        AIAgent agent = client.AsAIAgent(model: "claude-haiku-5-5", name: "compatibility-probe");

        Assert.Equal("compatibility-probe", agent.Name);
        Assert.Equal(13, typeof(AnthropicClient).Assembly.GetName().Version!.Major);
    }
}
