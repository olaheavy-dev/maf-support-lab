using Support.Api.Modules.Technical.Domain;

namespace Support.Api.Modules.Technical.Application;

/// <summary>Where troubleshooting articles are read from. Today the synthetic fixtures.</summary>
public interface IKnowledgeBase
{
    IReadOnlyList<KnowledgeArticle> Articles { get; }
}
