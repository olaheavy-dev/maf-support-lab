using Support.Api.Common.Domain;

namespace Support.Api.Common.Presentation;

public static class ResultExtensions
{
    /// <summary>Turns a result into a response: one branch for success, one for failure. No branch can be forgotten.</summary>
    public static TOut Match<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return result.IsSuccess ? onSuccess(result.Value) : onFailure(result.Error);
    }
}
