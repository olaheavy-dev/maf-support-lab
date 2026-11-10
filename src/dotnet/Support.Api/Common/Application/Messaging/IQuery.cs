namespace Support.Api.Common.Application.Messaging;

/// <summary>
/// A request that only reads. <typeparamref name="TResponse"/> only ties the query to its handler's result type.
/// </summary>
#pragma warning disable CA1040 // Marker interface: it links a query to its handler at compile time.
public interface IQuery<TResponse>;
#pragma warning restore CA1040
