using Support.Api.Modules.Triage.Domain;

namespace Support.Api.Modules.Triage.Application;

/// <summary>Triage a customer message and run whichever specialist the routing rules choose.</summary>
public interface ISupportRouter
{
    Task<SupportReply> RouteAsync(RoutingRequest request, CancellationToken cancellationToken);
}
