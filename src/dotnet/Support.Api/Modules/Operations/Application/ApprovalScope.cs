using Support.Api.Modules.Operations.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>
/// The approved operation a resumed run is allowed to execute, in trusted server context (never from the model).
/// A write tool runs only if the scope holds an operation of its kind with exactly its arguments.
/// </summary>
public static class ApprovalScope
{
    private static readonly AsyncLocal<Operation?> Approved = new();

    public static Operation? Current => Approved.Value;

    public static IDisposable Begin(Operation operation)
    {
        Approved.Value = operation;
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => Approved.Value = null;
    }
}
