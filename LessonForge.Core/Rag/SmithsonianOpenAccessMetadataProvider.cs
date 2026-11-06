// Copyright (c) 2026 — LessonForge. Original work.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LessonForge.Core.Rag;

/// <summary>
/// Anonymous, bounded use of the Smithsonian's OFFICIAL public Open Access S3
/// metadata files. This is a sample of one topical unit's JSON-lines shards,
/// not a comprehensive museum search. Only EDAN records explicitly marking
/// their METADATA as CC0 are eligible; images, media and attachments are ignored.
/// The API requiring a data.gov key is not used.
/// </summary>
public sealed class SmithsonianOpenAccessMetadataProvider(HttpClient http, string? cacheDirectory = null)
    : IEducationalSourceProvider
{
    public const string ProviderId = "smithsonian-openaccess-metadata";
    public const string BucketHost = "smithsonian-open-access.s3-us-west-2.amazonaws.com";
    public const string Prefix = "https://" + BucketHost + "/metadata/edan/";

    // Shards are distributed by record hash rather than topic. We scan at
    // most eight deterministic slices from one topical unit on each run.
    private static readonly string[] s_shards =
        ["00", "20", "40", "60", "80", "a0", "c0", "e0"];

    private static readonly HashSet<string> s_stopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "about", "which", "while", "what", "when", "where", "with", "from",
            "there", "their", "your", "this", "that", "these", "those",
            "explain", "overview", "lesson", "teach", "learning",
            "the", "and", "for", "has", "have", "had", "was", "were",
            "how", "why", "who", "does", "did", "are"
        };

    private readonly string _cacheRoot = cacheDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LessonForge", "Smithsonian");
    private static readonly SemaphoreSlim s_cacheLock = new(1, 1);

    public string Id => ProviderId;
    public int MaxResults { get; init; } = 3;
    public int MaxShards { get; init; } = 8;
    public int MaxBytesPerShard { get; init; } = 1_000_000;
    public TimeSpan TimeoutPerShard { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromDays(7);

    public bool SupportsTopic(string topic) => Terms(topic).Count > 0;

    public async Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
        string topic, CancellationToken ct = default)
    {
        var terms = Terms(topic);
        if (terms.Count == 0) return [];
        if (MaxResults is < 1 or > 6 || MaxShards is < 1 or > 8 ||
            MaxBytesPerShard is < 1024 or > 3_000_000 ||
            TimeoutPerShard <= TimeSpan.Zero || CacheLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Smithsonian bulk metadata limits are invalid.");

        var unit = ChooseUnit(topic);
        var records = new List<(SourceDocument Document, int Relevance)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shard in s_shards.Take(MaxShards))
        {
            ct.ThrowIfCancellationRequested();
            var data = await TryReadShardAsync(unit, shard, ct);
            if (data is null) continue;

            using var reader = new StringReader(data);
            string? line;
            var linesRead = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (++linesRead > 6_000) break;
                if (line.Length is < 10 or > 120_000) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var match = ParseEligible(document.RootElement, unit, terms);
                    if (match is { } candidate && seen.Add(candidate.Document.Url))
                        records.Add(candidate);
                }
                catch (JsonException)
                {
                    // A malformed independent JSONL record is not evidence,
                    // and it must not affect other eligible records.
                }
            }
        }

        return records
            .OrderByDescending(x => x.Relevance)
            .ThenBy(x => x.Document.CanonicalId, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(x => x.Document)
            .ToList();
    }

    private async Task<string?> TryReadShardAsync(
        string unit, string shard, CancellationToken ct)
    {
        var path = Path.Combine(_cacheRoot, unit, shard + ".txt");
        var url = $"{Prefix}{unit}/{shard}.txt";
        await s_cacheLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= CacheLifetime)
                return await File.ReadAllTextAsync(path, ct);

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeoutPerShard);
                using var response = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

                if (!response.IsSuccessStatusCode ||
                    response.RequestMessage?.RequestUri is not { } effective ||
                    effective.Scheme != Uri.UriSchemeHttps ||
                    !effective.Host.Equals(BucketHost, StringComparison.OrdinalIgnoreCase) ||
                    effective.AbsolutePath != $"/metadata/edan/{unit}/{shard}.txt" ||
                    effective.Query.Length > 0 ||
                    response.Content.Headers.ContentLength > MaxBytesPerShard)
                    return await ReadStaleAsync(path, ct);

                if (response.Content.Headers.ContentType?.MediaType is
                    not (null or "text/plain" or "application/json" or "application/octet-stream"))
                    return await ReadStaleAsync(path, ct);

                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                var block = new byte[16_384];
                int read;
                while ((read = await stream.ReadAsync(block, deadline.Token)) > 0)
                {
                    if (buffer.Length + read > MaxBytesPerShard)
                        return await ReadStaleAsync(path, ct);
                    buffer.Write(block, 0, read);
                }

                var data = Encoding.UTF8.GetString(buffer.ToArray());
                if (data.Length < 10) return await ReadStaleAsync(path, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, data, ct);
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                return data;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // This optional source must not make ordinary Wikipedia research
                // unavailable when a bulk shard is missing, busy or unreachable.
                return await ReadStaleAsync(path, ct);
            }
        }
        finally
        {
            s_cacheLock.Release();
        }
    }

    private static async Task<string?> ReadStaleAsync(string path, CancellationToken ct)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    private static (SourceDocument Document, int Relevance)? ParseEligible(
        JsonElement record, string unit, IReadOnlyList<string> terms)
    {
        if (record.ValueKind != JsonValueKind.Object ||
            String(record, "type") != "edanmdm" ||
            !String(record, "unitCode").Equals(unit, StringComparison.OrdinalIgnoreCase) ||
            !record.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("descriptiveNonRepeating", out var details) ||
            details.ValueKind != JsonValueKind.Object ||
            !details.TryGetProperty("metadata_usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            String(usage, "access") != "CC0")
            return null;

        // Only the museum's CC0-described metadata is retrieved/used. The
        // content may have media marked "Usage conditions apply" even when
        // metadata is CC0: do not ingest or quote any such media.
        var identifier = String(record, "url");
        if (string.IsNullOrEmpty(identifier))
        {
            var rawId = String(record, "id");
            identifier = rawId.StartsWith("edanmdm-", StringComparison.Ordinal)
                ? "edanmdm:" + rawId["edanmdm-".Length..]
                : rawId;
        }
        if (!identifier.StartsWith("edanmdm:" + unit + "_", StringComparison.OrdinalIgnoreCase) ||
            identifier.Length > 180 ||
            identifier.IndexOfAny(new[] { '?', '#', '/' }) >= 0 ||
            identifier.Skip("edanmdm:".Length).Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            return null;

        var title = Clean(String(record, "title"), 200);
        if (title.Length < 5) return null;
        var searchText = new StringBuilder(title);
        var metadata = new StringBuilder();
        if (content.TryGetProperty("freetext", out var freetext) &&
            freetext.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "date", "name", "objectType", "topic", "notes", "physicalDescription" })
            {
                if (!freetext.TryGetProperty(field, out var elements) ||
                    elements.ValueKind != JsonValueKind.Array) continue;
                var count = 0;
                foreach (var entry in elements.EnumerateArray())
                {
                    if (count++ >= 2) break;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var value = Clean(String(entry, "content"), 220);
                    if (value.Length == 0) continue;
                    metadata.Append(field).Append(": ").Append(value).Append(". ");
                    searchText.Append(' ').Append(value);
                }
            }
        }

        var score = terms.Count(term =>
            searchText.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)) * 6;
        if (terms.Count > 1 && score < 12 || score < 6)
            return null;

        var canonical = "https://collections.si.edu/search/detail/" + identifier;
        var description =
            "Smithsonian Open Access CC0 CATALOG METADATA ONLY, not an object's full " +
            "primary source, exhibition essay or media content. " +
            "Catalog title: " + title + ". Contributing unit: " + unit.ToUpperInvariant() +
            ". " + metadata +
            "The catalog description alone does not prove the contents or historical " +
            "interpretation of the underlying object.";
        return (new SourceDocument
        {
            Title = "Smithsonian Open Access catalog: " + title,
            Url = canonical,
            CanonicalId = identifier,
            Text = description,
            ProviderId = ProviderId,
            Kind = SourceKind.SmithsonianMetadata,
            Rights = ReviewedSourceRights.SmithsonianCc0Metadata,
            RetrievedAtUtc = DateTimeOffset.UtcNow
        }, score);
    }

    private static string String(JsonElement json, string key)
        => json.ValueKind == JsonValueKind.Object &&
           json.TryGetProperty(key, out var element) &&
           element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    private static string Clean(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = string.Join(' ', text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var cleaned = new string(normalized
            .Select(c => c is '<' or '>' or '{' or '}' ? ' ' : char.IsControl(c) ? ' ' : c)
            .ToArray()).Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max].TrimEnd() + "…";
    }

    private static List<string> Terms(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return [];
        return topic.ToLowerInvariant()
            .Split(topic.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3 && !s_stopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static string ChooseUnit(string topic)
    {
        if (new[] { "aircraft", "airplane", "space", "astronomy", "aviation", "moon",
            "apollo", "flight", "rocket" }.Any(w => topic.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return "nasm";
        if (new[] { "art", "artist", "painting", "sculpture", "portrait",
            "watercolor" }.Any(w => topic.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return "saam";
        if (new[] { "botany", "plant", "flowers", "photosynthesis", "flora",
            "chlorophyll" }.Any(w => topic.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return "nmnhbotany";
        return "nmah";
    }
}
