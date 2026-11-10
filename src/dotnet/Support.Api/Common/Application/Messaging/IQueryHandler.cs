using Support.Api.Common.Domain;

namespace Support.Api.Common.Application.Messaging;

/// <summary>Runs one query. One handler per use case keeps endpoints and tools thin.</summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}
