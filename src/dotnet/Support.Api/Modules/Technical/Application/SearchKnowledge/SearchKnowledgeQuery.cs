using Support.Api.Common.Application.Messaging;
using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Application.SearchKnowledge;

/// <summary>Find articles for a described problem. An empty result is a real answer: no documented fix.</summary>
public sealed record SearchKnowledgeQuery(string Text) : IQuery<IReadOnlyList<KnowledgeArticle>>;
