using System.Text.RegularExpressions;
using Support.Api.Common.Application.Messaging;
using Support.Api.Common.Domain;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Application.SearchKnowledge;

/// <summary>Keyword search over the knowledge base: best matches first, at most three, nothing below one keyword.</summary>
internal sealed partial class SearchKnowledgeQueryHandler(IKnowledgeBase knowledge)
    : IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<KnowledgeArticle>>
{
    private const int MaxResults = 3;

    public const int MaxQueryLength = 200;

    public Task<Result<IReadOnlyList<KnowledgeArticle>>> Handle(SearchKnowledgeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Tool arguments are model output (L14): bounded and printable, or rejected.
        var text = query.Text ?? string.Empty;
        if (text.Length > MaxQueryLength || text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<KnowledgeArticle>>(Error.Validation("invalid_query", $"The search query must be at most {MaxQueryLength} printable characters.")));
        }

        var terms = Terms().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<KnowledgeArticle> matches =
        [
            .. knowledge.Articles
                .Select(article => (Article: article, Score: article.Score(terms)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Article.ArticleId, StringComparer.Ordinal)
                .Take(MaxResults)
                .Select(match => match.Article),
        ];
        return Task.FromResult(Result.Success(matches));
    }

    [GeneratedRegex("[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*")]
    private static partial Regex Terms();
}
