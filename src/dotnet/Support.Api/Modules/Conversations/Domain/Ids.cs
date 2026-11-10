using System.Security.Cryptography;

namespace Support.Api.Modules.Conversations.Domain;

public static class Ids
{
    /// <summary>An unguessable id with a readable prefix, for example conv_3f9a… or turn_8c21….</summary>
    public static string New(string prefix) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
}
