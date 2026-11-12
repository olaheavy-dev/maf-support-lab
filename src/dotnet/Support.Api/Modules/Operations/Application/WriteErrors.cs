using Support.Api.Common.Domain;

namespace Support.Api.Modules.Operations.Application;

/// <summary>Why a write cannot happen. Codes match the contract's operation failure codes where they overlap.</summary>
public static class WriteErrors
{
    public static Error NotEligible(string detail) => Error.Conflict("not_eligible", detail);

    public static readonly Error NotFound = Error.NotFound("not_found", "The order does not exist or is not available to you.");

    public static Error InvalidArguments(string detail) => Error.Validation("invalid_arguments", detail);

    /// <summary>A write asked for outside a support conversation (DevUI chats only): there is nothing to bind an approval to.</summary>
    public static readonly Error NotInConversation = Error.Validation("not_in_conversation", "Changes can only be requested in a support conversation.");

    /// <summary>The reply for <see cref="NotInConversation"/>, written by code so the model's wording can't confuse it.</summary>
    public const string NotInConversationReply = "Changes can't be made from here. Ask for it in a support conversation, where you can approve it. Nothing has been changed.";
}
