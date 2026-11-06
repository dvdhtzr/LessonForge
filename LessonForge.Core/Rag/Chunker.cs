// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;

namespace LessonForge.Core.Rag;

/// <summary>
/// Splits source documents into overlapping, roughly token-sized chunks. Deterministic and
/// dependency-free so it can be unit-tested without a model. Splitting is paragraph-aware: it packs
/// whole paragraphs into a window up to <see cref="MaxChars"/>, carrying <see cref="OverlapChars"/>
/// of trailing context into the next window so a fact split across a boundary stays retrievable.
/// A single paragraph longer than the window is hard-split.
/// </summary>
public sealed class Chunker
{
    /// <summary>Target maximum characters per chunk (~1 token ≈ 4 chars, so ~450 tokens).</summary>
    public int MaxChars { get; init; } = 1800;

    /// <summary>Characters of overlap carried between adjacent chunks.</summary>
    public int OverlapChars { get; init; } = 200;

    /// <summary>Paragraphs shorter than this are dropped as noise (nav crumbs, stray headings).</summary>
    public int MinParagraphChars { get; init; } = 40;

    public IReadOnlyList<SourceChunk> Chunk(SourceDocument document)
    {
        var chunks = new List<SourceChunk>();
        var index = 0;

        foreach (var window in BuildWindows(document.Text))
        {
            chunks.Add(new SourceChunk
            {
                Id = $"{Stable(document.Url)}-{index++}",
                Text = window,
                SourceTitle = document.Title,
                SourceUrl = document.Url,
                Kind = document.Kind
            });
        }

        return chunks;
    }

    public IReadOnlyList<SourceChunk> ChunkAll(IEnumerable<SourceDocument> documents)
        => documents.SelectMany(Chunk).ToList();

    private IEnumerable<string> BuildWindows(string text)
    {
        var paragraphs = SplitParagraphs(text);
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            // A paragraph that alone exceeds the window is hard-split.
            if (paragraph.Length > MaxChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                foreach (var slice in HardSplit(paragraph))
                    yield return slice;
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 2 > MaxChars)
            {
                var finished = current.ToString();
                yield return finished;
                current.Clear();
                var carry = Tail(finished, OverlapChars);
                if (carry.Length > 0)
                    current.Append(carry).Append("\n\n");
            }

            if (current.Length > 0)
                current.Append("\n\n");
            current.Append(paragraph);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private IEnumerable<string> SplitParagraphs(string text)
    {
        var parts = text.Replace("\r\n", "\n")
            .Split(["\n\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in parts)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length >= MinParagraphChars)
                yield return trimmed;
        }
    }

    private IEnumerable<string> HardSplit(string paragraph)
    {
        var step = Math.Max(1, MaxChars - OverlapChars);
        for (var start = 0; start < paragraph.Length; start += step)
        {
            var length = Math.Min(MaxChars, paragraph.Length - start);
            yield return paragraph.Substring(start, length);
            if (start + length >= paragraph.Length)
                yield break;
        }
    }

    private static string Tail(string text, int count)
        => text.Length <= count ? text : text[^count..];

    // A short, stable, filesystem/id-safe token derived from a URL, for chunk ids.
    private static string Stable(string url)
    {
        unchecked
        {
            var hash = 23U;
            foreach (var c in url)
                hash = hash * 31 + c;
            return hash.ToString("x8");
        }
    }
}
