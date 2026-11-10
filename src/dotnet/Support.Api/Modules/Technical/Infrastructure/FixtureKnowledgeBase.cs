using System.Text.Json;
using System.Text.Json.Serialization;
using Support.Api.Modules.Technical.Application;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Infrastructure;

/// <summary>Read-only articles from the synthetic fixtures shared with the Python backend.</summary>
internal sealed class FixtureKnowledgeBase(IReadOnlyList<KnowledgeArticle> articles) : IKnowledgeBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public IReadOnlyList<KnowledgeArticle> Articles { get; } = articles;

    public static FixtureKnowledgeBase Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "fixtures");
        using var stream = File.OpenRead(Path.Combine(directory, "knowledge-base.json"));
        var file = JsonSerializer.Deserialize<KnowledgeFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("fixtures/knowledge-base.json is empty.");
        return new FixtureKnowledgeBase(file.Articles);
    }

    private sealed record KnowledgeFile(string FixtureVersion, IReadOnlyList<KnowledgeArticle> Articles);
}
