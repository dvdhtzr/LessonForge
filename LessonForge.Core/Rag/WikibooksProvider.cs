// Copyright (c) 2026 — LessonForge. Original work.

using System.Net;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;

namespace LessonForge.Core.Rag;

/// <summary>
/// Bounded text-only Wikibooks retrieval through the official anonymous
/// MediaWiki Action API. No web scraping, images, file downloads, external
/// links, embedded references or whole-book crawling.
/// </summary>
public sealed class WikibooksProvider(HttpClient http) : IEducationalSourceProvider
{
    public string Id => "wikibooks-text";
    private const string Endpoint = "https://en.wikibooks.org/w/api.php";

    public int MaxSearchResults { get; init; } = 6;
    public int MaxPages { get; init; } = 2;
    public int MaxResponseBytes { get; init; } = 600_000;

    public bool SupportsTopic(string topic) => !string.IsNullOrWhiteSpace(topic);

    public async Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
        string topic, CancellationToken ct = default)
    {
        if (!SupportsTopic(topic)) return [];
        if (MaxSearchResults is < 1 or > 10 || MaxPages is < 1 or > 4 ||
            MaxResponseBytes is < 4096 or > 1_000_000)
            throw new InvalidOperationException("Wikibooks retrieval limits are invalid.");

        var searchUrl = Build(
            ("action", "query"),
            ("list", "search"),
            ("srsearch", topic.Trim()),
            ("srnamespace", "0"),
            ("srlimit", MaxSearchResults.ToString()),
            ("format", "json"));

        using var response = await ReadJsonAsync(searchUrl, ct);
        if (response is null ||
            !response.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("search", out var hits) ||
            hits.ValueKind != JsonValueKind.Array)
            return [];

        var titles = hits.EnumerateArray()
            .Where(hit => hit.ValueKind == JsonValueKind.Object &&
                          hit.TryGetProperty("title", out var title) &&
                          title.ValueKind == JsonValueKind.String)
            .Select(hit => hit.GetProperty("title").GetString() ?? "")
            .Where(IsEligibleTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPages)
            .ToArray();

        var documents = new List<SourceDocument>(titles.Length);
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();
            var pageUrl = Build(
                ("action", "parse"),
                ("page", title),
                ("prop", "text"),
                ("formatversion", "2"),
                ("format", "json"));

            using var pageResponse = await ReadJsonAsync(pageUrl, ct);
            if (pageResponse is null ||
                !pageResponse.RootElement.TryGetProperty("parse", out var parse) ||
                parse.ValueKind != JsonValueKind.Object ||
                !parse.TryGetProperty("title", out var resolved) ||
                resolved.ValueKind != JsonValueKind.String ||
                !parse.TryGetProperty("text", out var textNode) ||
                textNode.ValueKind != JsonValueKind.String)
                continue;

            var resolvedTitle = resolved.GetString() ?? "";
            if (!IsEligibleTitle(resolvedTitle) ||
                !string.Equals(title, resolvedTitle, StringComparison.OrdinalIgnoreCase))
                continue; // Redirects cannot silently move to a different subject.

            var plainText = ExtractInstructionalText(textNode.GetString() ?? "");
            if (plainText.Length < 120) continue;

            documents.Add(new SourceDocument
            {
                Title = resolvedTitle,
                Url = "https://en.wikibooks.org/wiki/" +
                    Uri.EscapeDataString(resolvedTitle.Replace(' ', '_')),
                Text = plainText,
                ProviderId = Id,
                CanonicalId = resolvedTitle,
                Kind = SourceKind.WikibooksPage,
                Rights = ReviewedSourceRights.WikibooksPageText,
                RetrievedAtUtc = DateTimeOffset.UtcNow
            });
        }

        return documents;
    }

    private static bool IsEligibleTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            return false;
        // Namespace-zero instructional chapters are the only approved route.
        // Avoid wiki policy, portals, talk, user pages and templates even if a
        // search fixture or API anomaly returns them.
        return !new[] { "Wikibooks:", "User:", "User talk:", "Talk:", "File:",
                "Template:", "Category:", "Help:", "Special:", "MediaWiki:" }
            .Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only visible instructional text nodes; excluded HTML never enters embeddings.</summary>
    public static string ExtractInstructionalText(string html)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 800_000)
            return "";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode.SelectSingleNode(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' mw-parser-output ')]");
        if (root is null) return "";

        var remove = root.SelectNodes(
            ".//script|.//style|.//noscript|.//table|.//nav|.//aside|.//footer|" +
            ".//figure|.//figcaption|.//sup|.//audio|.//video|.//img|" +
            ".//form|.//math|.//svg|.//div[contains(@class, 'navbox')]");
        if (remove is not null)
            foreach (var node in remove.ToArray())
                node.Remove();

        var blocks = root.SelectNodes(".//p|.//h2|.//h3|.//h4|.//li|.//pre");
        if (blocks is null) return "";

        var paragraphs = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Ancestors("li").Any() && block.Name != "li")
                continue;
            var words = WebUtility.HtmlDecode(block.InnerText);
            var collapsed = string.Join(" ", words.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (collapsed.Length >= 40 && !paragraphs.Contains(collapsed, StringComparer.Ordinal))
                paragraphs.Add(collapsed);
            if (paragraphs.Sum(p => p.Length) >= 28_000) break;
        }

        return string.Join("\n\n", paragraphs);
    }

    private static string Build(params (string Name, string Value)[] query)
        => Endpoint + "?" + string.Join("&", query.Select(x =>
            Uri.EscapeDataString(x.Name) + "=" + Uri.EscapeDataString(x.Value)));

    private async Task<JsonDocument?> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength > MaxResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
