using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;

namespace Support.Api.Tests.Architecture;

/// <summary>
/// Proves every layering rule can fail: each production rule, run over the deliberately broken fixtures,
/// reports exactly the types that break it. A rule that never fails would protect nothing.
/// </summary>
public sealed class RuleSelfTests
{
    private const string Root = "Support.Api.Tests.Architecture.Fixtures";

    private static readonly ArchUnitNET.Domain.Architecture Fixtures =
        new ArchLoader().LoadAssemblies(typeof(RuleSelfTests).Assembly, typeof(Program).Assembly).Build();

    [Theory]
    [InlineData(LayerRules.Domain, "DomainUsesInfrastructure")]
    [InlineData(LayerRules.Application, "ApplicationUsesPresentation")]
    [InlineData(LayerRules.Presentation, "PresentationUsesInfrastructure")]
    public void Layer_rule_reports_the_type_using_a_forbidden_layer(string layer, string offender) =>
        Assert.Equal([offender], Violators(LayerRules.MustNotUse(Root, layer)));

    [Fact]
    public void Handler_rule_reports_unsealed_misnamed_and_misplaced_handlers() =>
        Assert.Equal(
            ["BadlyNamedProcessor", "MisplacedQueryHandler", "UnsealedQueryHandler"],
            Violators(LayerRules.HandlersAreSealedApplicationClasses(Root)));

    [Fact]
    public void Message_rule_reports_an_unsealed_query() =>
        Assert.Equal(["UnsealedQuery"], Violators(LayerRules.MessagesAreSealedApplicationTypes(Root)));

    [Fact]
    public void Placement_rule_reports_types_outside_the_layers_including_the_old_layout() =>
        Assert.Equal(["LooseHelper", "OldLayoutService"], Violators(LayerRules.EveryTypeIsInALayer(Root)));

    [Fact]
    public void Agents_rule_reports_an_agents_type_using_a_feature_module() =>
        Assert.Equal(["AgentUsesFeatureModule"], Violators(LayerRules.AgentsDependOnNoOtherModule(Root)));

    private static string[] Violators(IArchRule rule) =>
        [.. rule.Evaluate(Fixtures)
            .Where(result => !result.Passed)
            .Select(result => ((ArchUnitNET.Domain.IType)result.EvaluatedObject).Name)
            .Distinct()
            .Order(StringComparer.Ordinal)];
}
