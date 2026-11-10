using Microsoft.Extensions.DependencyInjection;
using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Technical.Application.SearchKnowledge;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Tests.Modules.Technical;

/// <summary>L08: knowledge search finds documented fixes and admits when there is none.</summary>
public sealed class TechnicalTests
{
    [Theory]
    [InlineData("my headphones keep crackling", "KB-102")]
    [InlineData("headphone static", "KB-102")]
    [InlineData("speaker won't connect to wifi", "KB-101")]
    [InlineData("the laptop stand wobbles", "KB-103")]
    public async Task Search_finds_the_article_for_a_described_problem(string text, string articleId)
    {
        var articles = await SearchAsync(text);

        Assert.Equal(articleId, articles[0].ArticleId);
    }

    [Theory]
    [InlineData("my smart fridge firmware shows error Z-77")]
    [InlineData("")]
    public async Task Search_returns_nothing_rather_than_a_loose_match(string text)
    {
        // "smart" appears in KB-101's title ("Smart Speaker") but not in its keywords: titles are not searched.
        Assert.Empty(await SearchAsync(text));
    }

    [Fact]
    public async Task An_unresolved_issue_offers_a_human_and_creates_nothing()
    {
        await using var factory = SupportApiFactory.Create(principal: "CUST-1001");
        var api = new ApiClient(factory.CreateClient());

        var turn = await api.AskAsync(await api.CreateConversationAsync(), "My smart fridge firmware shows error Z-77.");

        Assert.Equal("technical", turn.GetProperty("route").GetProperty("target").GetString());
        Assert.Equal("escalation_offer", turn.ReplyKind());
        Assert.Empty(turn.GetProperty("operations").EnumerateArray());
        Assert.DoesNotContain("ticket", turn.ReplyText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<KnowledgeArticle>> SearchAsync(string text)
    {
        using var factory = SupportApiFactory.Create();
        var handler = factory.Services.GetRequiredService<IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<KnowledgeArticle>>>();
        var result = await handler.Handle(new SearchKnowledgeQuery(text), TestContext.Current.CancellationToken);
        return result.Value;
    }
}
