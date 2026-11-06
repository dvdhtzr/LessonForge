// Copyright (c) 2026 — LessonForge. Original work.

namespace LessonForge.Core.Rag;

/// <summary>Where a piece of retrieved text came from.</summary>
public enum SourceKind
{
    /// <summary>Body text of a Wikipedia article.</summary>
    WikipediaArticle,

    /// <summary>A freely-fetchable external source cited by a Wikipedia article.</summary>
    Reference,

    /// <summary>USGS official earthquake event observations (not third-party reports).</summary>
    UsgsEarthquake,

    /// <summary>Public-domain congressional history catalog descriptions, not primary document text.</summary>
    LocCongressionalCatalog,

    /// <summary>Public-domain Project Gutenberg catalog metadata, never ebook text.</summary>
    GutenbergCatalog,

    /// <summary>CC0 Smithsonian EDAN catalog metadata only, not collection media.</summary>
    SmithsonianMetadata
}

/// <summary>A whole retrieved document before it is split into chunks.</summary>
public sealed class SourceDocument
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string Text { get; init; }
    public SourceKind Kind { get; init; }

    /// <summary>Stable provider and document identity, separate from source credibility.</summary>
    public string ProviderId { get; init; } = "";
    public string CanonicalId { get; init; } = "";
    public DateTimeOffset RetrievedAtUtc { get; init; }
    public SourceRights? Rights { get; init; }

    /// <summary>For references: the article that cited this source (for attribution context).</summary>
    public string? CitedBy { get; init; }
}

/// <summary>
/// A chunk of a source document, the unit that is embedded and retrieved. Every chunk keeps the
/// citation fields needed to attribute a fact back to its origin.
/// </summary>
public sealed class SourceChunk
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required string SourceTitle { get; init; }
    public required string SourceUrl { get; init; }
    public SourceKind Kind { get; init; }

    public string ProviderId { get; init; } = "";
    public string CanonicalId { get; init; } = "";
    public DateTimeOffset RetrievedAtUtc { get; init; }
    public string LicenseId { get; init; } = "";
    public string RightsPolicyUrl { get; init; } = "";
    public string DocumentSha256 { get; init; } = "";
}

/// <summary>A chunk paired with its similarity score from a vector search.</summary>
public sealed record ScoredChunk(SourceChunk Chunk, double Score);

/// <summary>A citation surfaced to agents and the final lesson/quiz.</summary>
public sealed class Citation
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string ProviderId { get; init; } = "";
    public string LicenseId { get; init; } = "";
    public string RightsPolicyUrl { get; init; } = "";
    public string Attribution { get; init; } = "";
    public string DocumentSha256 { get; init; } = "";
}
