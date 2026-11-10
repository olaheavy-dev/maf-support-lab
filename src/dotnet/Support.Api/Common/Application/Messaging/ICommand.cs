namespace Support.Api.Common.Application.Messaging;

/// <summary>
/// A request that changes state. <typeparamref name="TResponse"/> only ties the command to its handler's result type.
/// </summary>
#pragma warning disable CA1040 // Marker interface: it links a command to its handler at compile time.
public interface ICommand<TResponse>;
#pragma warning restore CA1040
