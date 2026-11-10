using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;

namespace Support.Api.Tests.Architecture;

/// <summary>The API obeys the layering rules.</summary>
public sealed class LayerTests
{
    private const string Root = "Support.Api";

    private static readonly ArchUnitNET.Domain.Architecture Api =
        new ArchLoader().LoadAssemblies(typeof(Program).Assembly).Build();

    [Theory]
    [InlineData(LayerRules.Domain)]
    [InlineData(LayerRules.Application)]
    [InlineData(LayerRules.Presentation)]
    public void Layer_does_not_use_forbidden_layers(string layer) =>
        LayerRules.MustNotUse(Root, layer).Check(Api);

    [Fact]
    public void Handlers_are_sealed_and_live_in_Application() =>
        LayerRules.HandlersAreSealedApplicationClasses(Root).Check(Api);

    [Fact]
    public void Commands_and_queries_are_sealed_and_live_in_Application() =>
        LayerRules.MessagesAreSealedApplicationTypes(Root).Check(Api);

    [Fact]
    public void Every_type_is_in_a_layer() =>
        LayerRules.EveryTypeIsInALayer(Root).Check(Api);

    [Fact]
    public void Agents_module_depends_on_no_feature_module() =>
        LayerRules.AgentsDependOnNoOtherModule(Root).Check(Api);

    [Fact]
    public void Every_module_has_its_layers()
    {
        // A rule over an empty layer would pass by matching nothing; prove each layer the rules rely on has types.
        string[] expected =
        [
            "Common.Domain", "Common.Application", "Common.Presentation",
            "Modules.Conversations.Domain", "Modules.Conversations.Application", "Modules.Conversations.Infrastructure", "Modules.Conversations.Presentation",
            "Modules.Orders.Domain", "Modules.Orders.Application", "Modules.Orders.Infrastructure", "Modules.Orders.Presentation",
            "Modules.Identity.Domain", "Modules.Identity.Application", "Modules.Identity.Infrastructure", "Modules.Identity.Presentation",
            "Modules.Agents.Application", "Modules.Agents.Infrastructure", "Modules.Agents.Presentation",
            "Modules.Triage.Domain", "Modules.Triage.Application", "Modules.Triage.Infrastructure",
            "Modules.Billing.Domain", "Modules.Billing.Application", "Modules.Billing.Infrastructure", "Modules.Billing.Presentation",
            "Modules.Technical.Domain", "Modules.Technical.Application", "Modules.Technical.Infrastructure", "Modules.Technical.Presentation",
        ];
        var namespaces = Api.Types.Select(type => type.Namespace.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.All(expected, layer => Assert.Contains(namespaces, ns => ns == $"{Root}.{layer}" || ns.StartsWith($"{Root}.{layer}.", StringComparison.Ordinal)));
    }
}
