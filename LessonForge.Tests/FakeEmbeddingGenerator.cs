using Microsoft.Extensions.AI;

namespace LessonForge.Tests;

/// <summary>
/// A deterministic, offline embedding generator for tests. It maps text to a vector by counting
/// occurrences of a small fixed vocabulary, so texts sharing keywords are close in vector space —
/// enough to exercise the vector index without a live model.
/// </summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly string[] Vocab =
    [
        "mitochondria", "cell", "energy", "photosynthesis", "plant", "chlorophyll",
        "light", "water", "oxygen", "glucose", "sun", "leaf"
    ];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
            list.Add(new Embedding<float>(Vectorize(value)));
        return Task.FromResult(list);
    }

    private static float[] Vectorize(string text)
    {
        var lower = text.ToLowerInvariant();
        var v = new float[Vocab.Length];
        for (var i = 0; i < Vocab.Length; i++)
            v[i] = CountOccurrences(lower, Vocab[i]);
        // Avoid an all-zero vector for unrelated text.
        v[^1] += 0.01f;
        return v;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
