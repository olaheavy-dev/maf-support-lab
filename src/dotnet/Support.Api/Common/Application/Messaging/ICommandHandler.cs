using Support.Api.Common.Domain;

namespace Support.Api.Common.Application.Messaging;

/// <summary>Runs one command. One handler per use case keeps endpoints and tools thin.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}
