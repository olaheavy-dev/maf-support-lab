using System.Diagnostics.CodeAnalysis;

namespace Support.Api.Common.Domain;

/// <summary>
/// The outcome of a use case: success, or failure with an <see cref="Error"/>. Expected failures such as
/// "not found" travel as values; exceptions are left for genuinely unexpected faults.
/// </summary>
public class Result
{
    private protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if ((isSuccess && error != Error.None) || (!isSuccess && error == Error.None))
        {
            throw new ArgumentException("A success result carries no error and a failure result carries one.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public sealed class Result<TValue> : Result
{
    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => Value = value;

    /// <summary>The value of a success. Reading it from a failure is a programming error and throws.</summary>
    [NotNull]
    [AllowNull]
    public TValue Value => IsSuccess
        ? field!
        : throw new InvalidOperationException("The value of a failure result can't be accessed.");
}
