using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Agents.Application;
using Support.Api.Modules.Technical.Application.SearchKnowledge;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Presentation;

/// <summary>
/// The technical agent's only capability: search documented fixes. There is deliberately no ticket tool; when nothing
/// matches, the reply offers escalation and nothing is created (tickets arrive with approval in L10).
/// </summary>
public sealed partial class KnowledgeSearchTool(
    IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<KnowledgeArticle>> search,
    ToolMetrics metrics,
    ILogger<KnowledgeSearchTool> logger)
{
    public const string Name = "search_knowledge_base";

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(SearchAsync, Name);

    [Description("Search VoltCart's troubleshooting articles for a product problem. Use the product and the symptom as the query, for example 'headphones crackling'.")]
    public async Task<KnowledgeSearchResult> SearchAsync(
        [Description("Product and symptom keywords, for example 'speaker wifi setup'.")] string query,
        CancellationToken cancellationToken = default)
    {
        var result = await search.Handle(new SearchKnowledgeQuery(query), cancellationToken).ConfigureAwait(false);
        var articles = result.IsSuccess ? result.Value : [];
        var outcome = articles.Count > 0 ? ToolOutcome.Found : ToolOutcome.NoMatch;
        var outcomeName = articles.Count > 0 ? "found" : "no_match";

        TurnActivity.Current?.Record(outcome);
        metrics.Record(Name, outcomeName);
        Log.SearchFinished(logger, Name, outcomeName, articles.Count);
        return new KnowledgeSearchResult(outcomeName, [.. articles.Select(article => new ArticleResult(article.ArticleId, article.Title, article.Steps))]);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} finished with outcome {Outcome} ({Count} articles)")]
        public static partial void SearchFinished(ILogger logger, string tool, string outcome, int count);
    }
}

public sealed record KnowledgeSearchResult(
    [property: JsonPropertyName("search")] string Search,
    [property: JsonPropertyName("articles")] IReadOnlyList<ArticleResult> Articles);

public sealed record ArticleResult(
    [property: JsonPropertyName("article_id")] string ArticleId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("steps")] IReadOnlyList<string> Steps);
