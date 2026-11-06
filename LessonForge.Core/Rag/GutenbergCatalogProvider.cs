// Copyright (c) 2026 — LessonForge. Original work.

using System.Globalization;
using System.Text;

namespace LessonForge.Core.Rag;

/// <summary>
/// Searches the official Project Gutenberg machine-readable CSV catalog,
/// downloaded no more often than weekly into a local cache. It contributes
/// bibliographic evidence only. It NEVER crawls Gutenberg pages, downloads
/// individual ebooks, or treats a catalog row as a book-text copyright license.
/// </summary>
public sealed class GutenbergCatalogProvider(HttpClient http, string? cacheDirectory = null)
    : IEducationalSourceProvider
{
    public const string ProviderId = "gutenberg-catalog";
    public const string FeedUrl =
        "https://www.gutenberg.org/cache/epub/feeds/pg_catalog.csv";

    private static readonly SemaphoreSlim s_cacheLock = new(1, 1);
    private static readonly HashSet<string> s_stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "with", "from", "this", "that", "them", "these", "those", "into",
        "what", "when", "where", "which", "whose", "would", "could", "should",
        "the", "and", "for", "are", "was", "were", "has", "have", "had",
        "how", "why", "who", "does", "did", "can", "you", "your", "lesson",
        "teach", "explain", "introduction", "overview"
    };

    private readonly string _cacheDirectory = cacheDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LessonForge", "Gutenberg");

    public string Id => ProviderId;
    public int MaxResults { get; init; } = 3;
    public int MaxDownloadBytes { get; init; } = 35_000_000;
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(40);

    public bool SupportsTopic(string topic)
        => GetTerms(topic).Count > 0;

    public async Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
        string topic, CancellationToken ct = default)
    {
        var terms = GetTerms(topic);
        if (terms.Count == 0) return [];
        if (MaxResults is < 1 or > 8 || MaxDownloadBytes is < 2048 or > 75_000_000 ||
            CacheLifetime <= TimeSpan.Zero || Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Project Gutenberg catalog limits are invalid.");

        var bytes = await ReadCatalogAsync(ct);
        if (bytes is null) return [];

        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var entries = ParseEntries(text, ct);
        var ranked = entries
            .Select(entry => (Entry: entry, Score: Score(entry, terms, topic)))
            .Where(x => x.Score >= 6)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Id)
            .Take(MaxResults)
            .ToArray();

        return ranked.Select(x =>
        {
            var item = x.Entry;
            var summary = new StringBuilder();
            summary.Append("Project Gutenberg CATALOG METADATA ONLY; this is NOT the ebook's text. ");
            summary.Append("Catalog ebook number: ").Append(item.Id).Append(". ");
            summary.Append("Bibliographic title: ").Append(item.Title).Append(". ");
            if (item.Authors.Length > 0)
                summary.Append("Listed author(s): ").Append(item.Authors).Append(". ");
            if (item.Subjects.Length > 0)
                summary.Append("Catalog subject headings: ").Append(item.Subjects).Append(". ");
            if (item.Bookshelves.Length > 0)
                summary.Append("Catalog bookshelves: ").Append(item.Bookshelves).Append(". ");
            if (item.Issued.Length > 0)
                summary.Append("Project Gutenberg release date (NOT the original publication date): ")
                    .Append(item.Issued).Append(". ");
            summary.Append("A catalog description does not establish the content of the book, ");
            summary.Append("whether this edition or translation is public domain, or whether ");
            summary.Append("its full text may be processed by AI.");

            return new SourceDocument
            {
                Title = $"Project Gutenberg catalog: {item.Title}",
                Url = $"https://www.gutenberg.org/ebooks/{item.Id}",
                CanonicalId = item.Id.ToString(CultureInfo.InvariantCulture),
                Kind = SourceKind.GutenbergCatalog,
                ProviderId = ProviderId,
                Rights = ReviewedSourceRights.GutenbergCatalogMetadata,
                RetrievedAtUtc = DateTimeOffset.UtcNow,
                Text = summary.ToString()
            };
        }).ToArray();
    }

    private async Task<byte[]?> ReadCatalogAsync(CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, "pg_catalog.csv");
        await s_cacheLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= CacheLifetime)
                return await File.ReadAllBytesAsync(path, ct);

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(Timeout);
                using var response = await http.GetAsync(
                    FeedUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode ||
                    response.RequestMessage?.RequestUri is not { } effective ||
                    effective.Scheme != Uri.UriSchemeHttps ||
                    !effective.Host.Equals("www.gutenberg.org", StringComparison.OrdinalIgnoreCase) ||
                    effective.AbsolutePath != "/cache/epub/feeds/pg_catalog.csv" ||
                    response.Content.Headers.ContentLength > MaxDownloadBytes)
                    return await ReadStaleIfPresentAsync(path, ct);

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not (null or "text/csv" or "application/csv" or "application/octet-stream" or "binary/octet-stream"))
                    return await ReadStaleIfPresentAsync(path, ct);

                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[16_384];
                int read;
                while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
                {
                    if (buffer.Length + read > MaxDownloadBytes)
                        return await ReadStaleIfPresentAsync(path, ct);
                    buffer.Write(bytes, 0, read);
                }
                var downloaded = buffer.ToArray();
                if (downloaded.Length < 64 || !Encoding.UTF8.GetString(downloaded.AsSpan(0, Math.Min(downloaded.Length, 512))).Contains("Text#", StringComparison.Ordinal))
                    return await ReadStaleIfPresentAsync(path, ct);

                Directory.CreateDirectory(_cacheDirectory);
                var tempPath = Path.Combine(_cacheDirectory,
                    $"pg_catalog.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(tempPath, downloaded, ct);
                    File.Move(tempPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                return downloaded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A temporary Gutenberg outage must not break a lesson whose
                // Wikipedia/USGS/LoC evidence is otherwise rights-eligible.
                return await ReadStaleIfPresentAsync(path, ct);
            }
        }
        finally
        {
            s_cacheLock.Release();
        }
    }

    private async Task<byte[]?> ReadStaleIfPresentAsync(string path, CancellationToken ct)
        => File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;

    private static List<BookEntry> ParseEntries(string content, CancellationToken ct)
    {
        using var reader = new StringReader(content);
        using var rows = ReadCsvRows(reader).GetEnumerator();
        if (!rows.MoveNext()) return [];
        var header = rows.Current;
        var columns = header.Select((name, index) => (name, index))
            .ToDictionary(x => x.name.Trim(), x => x.index, StringComparer.OrdinalIgnoreCase);
        var required = new[] { "Text#", "Type", "Title", "Language", "Authors", "Subjects",
            "Bookshelves", "Issued" };
        if (required.Any(name => !columns.ContainsKey(name)))
            throw new InvalidDataException("Gutenberg catalog is missing mandatory columns.");

        string Field(string[] row, string key)
        {
            var index = columns[key];
            return index < row.Length ? row[index].Trim() : "";
        }

        var entries = new List<BookEntry>();
        var rowsRead = 0;
        while (rows.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            if (++rowsRead > 150_000)
                throw new InvalidDataException("Gutenberg catalog row limit exceeded.");
            var row = rows.Current;
            if (!string.Equals(Field(row, "Type"), "Text", StringComparison.OrdinalIgnoreCase) ||
                !Field(row, "Language").Split(';').Any(lang => lang.Trim().Equals("en",
                    StringComparison.OrdinalIgnoreCase)) ||
                !int.TryParse(Field(row, "Text#"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id) ||
                id is < 1 or > 9_999_999)
                continue;

            var title = Clean(Field(row, "Title"), 260);
            if (title.Length < 3) continue;
            entries.Add(new BookEntry(
                id, title, Clean(Field(row, "Authors"), 300),
                Clean(Field(row, "Subjects"), 600),
                Clean(Field(row, "Bookshelves"), 400),
                Clean(Field(row, "Issued"), 40)));
        }
        return entries;
    }

    private static IEnumerable<string[]> ReadCsvRows(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        while (true)
        {
            var current = reader.Read();
            if (current == -1)
            {
                if (inQuotes)
                    throw new InvalidDataException("Unterminated Gutenberg catalog CSV string.");
                if (field.Length > 0 || fields.Count > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                yield break;
            }

            var c = (char)current;
            if (c == '"')
            {
                if (inQuotes && reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else
                    inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if ((c == '\r' || c == '\n') && !inQuotes)
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(field.ToString());
                field.Clear();
                if (fields.Any(x => x.Length > 0))
                    yield return fields.ToArray();
                fields.Clear();
            }
            else
            {
                field.Append(c);
                if (field.Length > 15_000)
                    throw new InvalidDataException("Gutenberg catalog CSV field exceeds limit.");
            }
        }
    }

    private static string Clean(string input, int limit)
    {
        var normalized = string.Join(' ', input.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var cleaned = new string(normalized
            .Select(c => c is '<' or '>' or '{' or '}' ? ' ' : char.IsControl(c) ? ' ' : c)
            .ToArray()).Trim();
        return cleaned.Length <= limit ? cleaned : cleaned[..limit].TrimEnd() + "…";
    }

    private static List<string> GetTerms(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return [];
        var words = topic.ToLowerInvariant().Split(
            topic.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries);
        return words.Where(word => word.Length >= 3 && !s_stopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static int Score(BookEntry book, IReadOnlyList<string> terms, string topic)
    {
        if (book.Title.Contains(topic.Trim(), StringComparison.OrdinalIgnoreCase))
            return 30;
        var score = 0;
        foreach (var term in terms)
        {
            if (book.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 7;
            if (book.Authors.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 5;
            if (book.Subjects.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 3;
            if (book.Bookshelves.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;
        }
        return score;
    }

    private sealed record BookEntry(
        int Id, string Title, string Authors, string Subjects, string Bookshelves, string Issued);
}
