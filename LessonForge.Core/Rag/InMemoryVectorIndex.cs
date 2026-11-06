// Copyright (c) 2026 — LessonForge. Original work.

using Microsoft.Extensions.AI;

namespace LessonForge.Core.Rag;

/// <summary>
/// A per-request, in-memory vector index. Chunks are embedded with the injected
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (Ollama's <c>nomic-embed-text</c> in
/// production) and searched by cosine similarity. By design this is built fresh for each
/// generation run and discarded afterwards — nothing is persisted — so a tiny hand-rolled index is
/// deliberately preferred over a heavier vector-store dependency. It is also trivially testable
/// with a fake embedding generator.
/// </summary>
public sealed class InMemoryVectorIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
    private readonly List<(float[] Vector, SourceChunk Chunk)> _records = [];

    /// <summary>How many chunks to embed per model call.</summary>
    public int EmbeddingBatchSize { get; init; } = 32;

    public int Count => _records.Count;

    /// <summary>Embeds and stores the given chunks. Can be called multiple times to accumulate.</summary>
    public async Task IngestAsync(IReadOnlyList<SourceChunk> chunks, CancellationToken ct = default)
    {
        for (var start = 0; start < chunks.Count; start += EmbeddingBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(start).Take(EmbeddingBatchSize).ToList();
            var vectors = await embeddings.GenerateAsync(batch.Select(c => c.Text), cancellationToken: ct);

            for (var i = 0; i < batch.Count; i++)
                _records.Add((Normalize(vectors[i].Vector.ToArray()), batch[i]));
        }
    }

    /// <summary>Returns the <paramref name="k"/> chunks most similar to <paramref name="query"/>.</summary>
    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(string query, int k = 6, CancellationToken ct = default)
    {
        if (_records.Count == 0)
            return [];

        var queryVector = await embeddings.GenerateVectorAsync(query, cancellationToken: ct);
        var q = Normalize(queryVector.ToArray());

        return _records
            .Select(r => new ScoredChunk(r.Chunk, Dot(q, r.Vector)))
            .OrderByDescending(s => s.Score)
            .Take(Math.Max(1, k))
            .ToList();
    }

    // Vectors are L2-normalized on insert and on query, so cosine similarity is a plain dot product.
    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return v;
        var inv = (float)(1.0 / norm);
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = v[i] * inv;
        return result;
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
