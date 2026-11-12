// Deliberately broken code. The self-tests run the production rules over this namespace and expect each
// rule to report exactly the type that breaks it, which proves the rules can fail.
#pragma warning disable CA1812, CA1822, CA1852, IDE0060

using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Broken.Infrastructure
{
    internal sealed class Store
    {
        public int Count => 0;
    }

    internal sealed class MisplacedQueryHandler : IQueryHandler<Application.FixtureQuery, int>
    {
        public Task<Result<int>> Handle(Application.FixtureQuery query, CancellationToken cancellationToken) => Task.FromResult(Result.Success(0));
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Broken.Presentation
{
    internal sealed class Endpoint
    {
        public int Get() => 0;
    }

    internal sealed class PresentationUsesInfrastructure
    {
        public int Read(Infrastructure.Store store) => store.Count;
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Broken.Domain
{
    internal sealed class DomainUsesInfrastructure
    {
        public int Read(Infrastructure.Store store) => store.Count;
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Broken.Application
{
    internal sealed record FixtureQuery : IQuery<int>;

    internal record UnsealedQuery : IQuery<int>;

    internal sealed class ApplicationUsesPresentation
    {
        public int Call(Presentation.Endpoint endpoint) => endpoint.Get();
    }

    internal class UnsealedQueryHandler : IQueryHandler<FixtureQuery, int>
    {
        public Task<Result<int>> Handle(FixtureQuery query, CancellationToken cancellationToken) => Task.FromResult(Result.Success(0));
    }

    internal sealed class BadlyNamedProcessor : IQueryHandler<FixtureQuery, int>
    {
        public Task<Result<int>> Handle(FixtureQuery query, CancellationToken cancellationToken) => Task.FromResult(Result.Success(0));
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Agents.Infrastructure
{
    // Infrastructure may use any layer, so this breaks only the Agents-independence rule.
    internal sealed class AgentUsesFeatureModule
    {
        public int Read(Broken.Infrastructure.Store store) => store.Count;
    }

    internal sealed class AgentUsesOnlyItself
    {
        public int Read(AgentUsesFeatureModule agent) => agent.GetHashCode();
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Modules.Broken
{
    internal sealed class LooseHelper
    {
    }

    internal static class BrokenModule
    {
    }
}

namespace Support.Api.Tests.Architecture.Fixtures.Orders
{
    // The old by-topic layout: outside Common and Modules entirely.
    internal sealed class OldLayoutService
    {
    }
}
