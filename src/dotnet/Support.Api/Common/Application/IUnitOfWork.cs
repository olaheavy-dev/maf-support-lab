namespace Support.Api.Common.Application;

/// <summary>
/// Runs work atomically against the durable store: every write made inside commits together, or none does (L11).
/// Used where a business change and its operation record must never be split, e.g. a refund and "committed".
/// </summary>
public interface IUnitOfWork
{
    Task<T> AtomicallyAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Thrown inside <see cref="IUnitOfWork.AtomicallyAsync{T}"/> to roll back without treating it as a failure.</summary>
public sealed class RollbackException : Exception
{
    public RollbackException()
    {
    }

    public RollbackException(string message)
        : base(message)
    {
    }

    public RollbackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
