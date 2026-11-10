namespace Support.Api.Modules.Technical.Domain;

/// <summary>A troubleshooting article (fixtures/data/knowledge-base.json). Public product help, not customer data.</summary>
public sealed record KnowledgeArticle(string ArticleId, string Title, IReadOnlyList<string> Keywords, IReadOnlyList<string> Steps)
{
    /// <summary>
    /// How many of this article's keywords the search terms mention. Only keywords count (not the title), so
    /// "smart fridge" does not match "Smart Speaker"; a term matches a keyword exactly or as a plural/inflection
    /// of at least four letters ("headphone" matches "headphones").
    /// </summary>
    public int Score(IReadOnlyCollection<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return Keywords.Count(keyword => terms.Any(term =>
            string.Equals(term, keyword, StringComparison.OrdinalIgnoreCase) ||
            (term.Length >= 4 && keyword.Length >= 4 &&
             (term.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) || keyword.StartsWith(term, StringComparison.OrdinalIgnoreCase)))));
    }
}
