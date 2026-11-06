using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public class ChunkerTests
{
    private static SourceDocument Doc(string text) => new()
    {
        Title = "Test Article",
        Url = "https://en.wikipedia.org/wiki/Test",
        Text = text,
        Kind = SourceKind.WikipediaArticle
    };

    [Fact]
    public void Chunks_carry_source_metadata()
    {
        var chunker = new Chunker { MaxChars = 200, OverlapChars = 40, MinParagraphChars = 10 };
        var text = string.Join("\n\n", Enumerable.Range(0, 10).Select(i =>
            $"Paragraph {i} about photosynthesis in plant cells and how chlorophyll captures light."));

        var chunks = chunker.Chunk(Doc(text));

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Equal("Test Article", c.SourceTitle);
            Assert.Equal("https://en.wikipedia.org/wiki/Test", c.SourceUrl);
            Assert.Equal(SourceKind.WikipediaArticle, c.Kind);
            Assert.False(string.IsNullOrWhiteSpace(c.Text));
        });
    }

    [Fact]
    public void Respects_max_size_when_packing_paragraphs()
    {
        var chunker = new Chunker { MaxChars = 120, OverlapChars = 20, MinParagraphChars = 5 };
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Short paragraph number {i}."));

        var chunks = chunker.Chunk(Doc(text));

        Assert.True(chunks.Count > 1);
        // Packed chunks stay within a small tolerance of MaxChars (overlap + one paragraph join).
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 220, $"chunk too long: {c.Text.Length}"));
    }

    [Fact]
    public void Hard_splits_a_single_oversized_paragraph()
    {
        var chunker = new Chunker { MaxChars = 100, OverlapChars = 20, MinParagraphChars = 5 };
        var giant = new string('x', 450);

        var chunks = chunker.Chunk(Doc(giant));

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
    }

    [Fact]
    public void Drops_noise_paragraphs_below_minimum()
    {
        var chunker = new Chunker { MaxChars = 500, OverlapChars = 20, MinParagraphChars = 30 };
        var text = "ok\n\nThis is a sufficiently long paragraph that should be kept in the output set.";

        var chunks = chunker.Chunk(Doc(text));

        Assert.Single(chunks);
        Assert.DoesNotContain("ok\n", chunks[0].Text);
    }
}
