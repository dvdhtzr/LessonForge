using LessonForge.Core.Rag;
using Xunit;

namespace LessonForge.Tests;

public class VectorIndexTests
{
    private static SourceChunk Chunk(string id, string text) => new()
    {
        Id = id,
        Text = text,
        SourceTitle = "src",
        SourceUrl = $"https://example.com/{id}",
        Kind = SourceKind.WikipediaArticle
    };

    [Fact]
    public async Task Search_returns_most_similar_chunk_first()
    {
        var index = new InMemoryVectorIndex(new FakeEmbeddingGenerator());
        await index.IngestAsync(
        [
            Chunk("a", "Mitochondria are the energy powerhouse of the cell."),
            Chunk("b", "Photosynthesis in a plant leaf uses light and chlorophyll."),
            Chunk("c", "Water and oxygen cycle through the environment."),
        ]);

        var results = await index.SearchAsync("how does photosynthesis use chlorophyll and light", k: 2);

        Assert.NotEmpty(results);
        Assert.Equal("b", results[0].Chunk.Id);
        Assert.True(results[0].Score >= results[^1].Score);
    }

    [Fact]
    public async Task Empty_index_returns_no_results()
    {
        var index = new InMemoryVectorIndex(new FakeEmbeddingGenerator());
        var results = await index.SearchAsync("anything");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Count_reflects_ingested_chunks()
    {
        var index = new InMemoryVectorIndex(new FakeEmbeddingGenerator());
        await index.IngestAsync([Chunk("a", "cell energy"), Chunk("b", "plant leaf")]);
        Assert.Equal(2, index.Count);
    }
}
