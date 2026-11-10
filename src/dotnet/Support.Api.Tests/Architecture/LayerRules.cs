using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using Support.Api.Common.Application.Messaging;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Support.Api.Tests.Architecture;

/// <summary>
/// The layering rules of docs/decisions/0002-code-layout.md. Layers are folders (namespaces) inside one project,
/// so the compiler can't stop one layer reaching into another; these rules do.
/// Each rule takes the root namespace so the self-tests can run the very same rule over deliberately broken fixtures.
/// </summary>
internal static class LayerRules
{
    public const string Domain = "Domain";
    public const string Application = "Application";
    public const string Infrastructure = "Infrastructure";
    public const string Presentation = "Presentation";

    private static readonly string[] AllLayers = [Domain, Application, Infrastructure, Presentation];

    /// <summary>Each layer and the layers it must never use. Everything else (including other modules' same layer) is allowed.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Forbidden = new Dictionary<string, string[]>
    {
        [Domain] = [Application, Infrastructure, Presentation],
        [Application] = [Infrastructure, Presentation],
        [Presentation] = [Infrastructure],
    };

    /// <summary>The types in a layer of Common or of any module, for example Support.Api.Modules.Orders.Domain.*.</summary>
    public static GivenTypesConjunction TypesIn(string root, params string[] layers) =>
        Types().That().ResideInNamespaceMatching(LayerPattern(root, layers));

    public static IArchRule MustNotUse(string root, string layer) =>
        TypesIn(root, layer).Should().NotDependOnAny(TypesIn(root, Forbidden[layer]))
            .Because($"{layer} must not use {string.Join(" or ", Forbidden[layer])}")
            .WithoutRequiringPositiveResults();

    /// <summary>Every handler is a sealed class named ...Handler in an Application layer.</summary>
    public static IArchRule HandlersAreSealedApplicationClasses(string root) =>
        // ArchUnitNET combines conditions left to right, so the Or comes first and the namespace filter applies to both.
        Classes().That().ImplementInterface(typeof(ICommandHandler<,>)).Or().ImplementInterface(typeof(IQueryHandler<,>))
            .And().ResideInNamespaceMatching($"^{Regex.Escape(root)}\\.")
            .Should().BeSealed()
            .AndShould().HaveNameEndingWith("Handler")
            .AndShould().ResideInNamespaceMatching(LayerPattern(root, Application))
            .Because("one sealed handler per use case lives in Application");

    /// <summary>Every command and query is a sealed type named ...Command or ...Query in an Application layer.</summary>
    public static IArchRule MessagesAreSealedApplicationTypes(string root) =>
        Classes().That().ImplementInterface(typeof(ICommand<>)).Or().ImplementInterface(typeof(IQuery<>))
            .And().ResideInNamespaceMatching($"^{Regex.Escape(root)}\\.")
            .Should().BeSealed()
            .AndShould().HaveNameMatching("(Command|Query)$")
            .AndShould().ResideInNamespaceMatching(LayerPattern(root, Application))
            .Because("commands and queries are immutable use-case inputs that live in Application");

    /// <summary>
    /// Nothing sits outside a layer: every type under the root namespace is in a Common or module layer folder,
    /// except each module's single XModule wiring class at the module root. This also catches code added in the
    /// old by-topic layout (for example Support.Api.Orders), which the dependency rules would otherwise never see.
    /// Program.cs is in the global namespace and is the only composition root outside the root namespace.
    /// </summary>
    public static IArchRule EveryTypeIsInALayer(string root) =>
        Types().That().ResideInNamespaceMatching($"^{Regex.Escape(root)}(\\..+)?$")
            .And().DoNotResideInNamespaceMatching(LayerPattern(root, AllLayers))
            .Should().ResideInNamespaceMatching($"^{Regex.Escape(root)}\\.Modules\\.[A-Za-z]+$")
            .AndShould().HaveNameEndingWith("Module")
            .Because("a file outside the layer folders hides which layer it belongs to")
            .WithoutRequiringPositiveResults();

    /// <summary>
    /// Agents is the shared AI runtime (model provider, agent collection, DevUI). Feature modules plug their agents
    /// into it, so it must never refer back to them; otherwise each new specialist would tangle the modules together.
    /// </summary>
    public static IArchRule AgentsDependOnNoOtherModule(string root) =>
        Types().That().ResideInNamespaceMatching($"^{Regex.Escape(root)}\\.Modules\\.Agents(\\..+)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching($"^{Regex.Escape(root)}\\.Modules\\.(?!Agents(\\.|$))[A-Za-z]+(\\..+)?$"))
            .Because("feature modules plug into Agents, never the other way round");

    private static string LayerPattern(string root, params string[] layers) =>
        $"^{Regex.Escape(root)}\\.(Common|Modules\\.[A-Za-z]+)\\.({string.Join('|', layers)})(\\..+)?$";
}
