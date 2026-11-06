// Copyright (c) 2026 — LessonForge. Original work.

using System.Net.Http.Json;
using System.Text.Json;

namespace LessonForge.Core.Rag;

/// <summary>
/// A thin client over the English Wikipedia MediaWiki Action API. It can find the best-matching
/// article(s) for a topic, pull an article's plain-text body, list the articles it links to, and
/// list the external URLs it cites. All calls go through an injected <see cref="HttpClient"/> so the
/// client is testable against recorded fixtures.
/// </summary>
public sealed class WikipediaClient(HttpClient http)
{
    /// <summary>Base MediaWiki API endpoint. English Wikipedia by default.</summary>
    public string ApiEndpoint { get; init; } = "https://en.wikipedia.org/w/api.php";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>Returns article titles best matching <paramref name="topic"/>, most relevant first.</summary>
    public async Task<IReadOnlyList<string>> SearchTitlesAsync(string topic, int limit = 3, CancellationToken ct = default)
    {
        var url = Build(new()
        {
            ["action"] = "query",
            ["list"] = "search",
            ["srsearch"] = topic,
            ["srlimit"] = limit.ToString(),
            ["srnamespace"] = "0",
            ["format"] = "json"
        });

        using var doc = await GetJsonAsync(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("query", out var query)
            || !query.TryGetProperty("search", out var search))
            return [];

        var titles = new List<string>();
        foreach (var hit in search.EnumerateArray())
            if (hit.TryGetProperty("title", out var t) && t.GetString() is { } title)
                titles.Add(title);
        return titles;
    }

    /// <summary>Fetches an article's plain-text body as a <see cref="SourceDocument"/>, or null if missing.</summary>
    public async Task<SourceDocument?> GetArticleAsync(string title, CancellationToken ct = default)
    {
        var url = Build(new()
        {
            ["action"] = "query",
            ["prop"] = "extracts|info",
            ["explaintext"] = "1",
            ["exsectionformat"] = "plain",
            ["inprop"] = "url",
            ["redirects"] = "1",
            ["titles"] = title,
            ["format"] = "json"
        });

        using var doc = await GetJsonAsync(url, ct);
        var page = FirstPage(doc);
        if (page is null || page.Value.TryGetProperty("missing", out _))
            return null;

        var extract = page.Value.TryGetProperty("extract", out var ex) ? ex.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(extract))
            return null;

        var canonical = page.Value.TryGetProperty("canonicalurl", out var cu) ? cu.GetString() : null;
        var resolvedTitle = page.Value.TryGetProperty("title", out var pt) ? pt.GetString() ?? title : title;

        return new SourceDocument
        {
            Title = resolvedTitle,
            Url = canonical ?? $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(resolvedTitle.Replace(' ', '_'))}",
            Text = extract,
            Kind = SourceKind.WikipediaArticle
        };
    }

    /// <summary>Returns titles of other articles this article links to (namespace 0 only).</summary>
    public async Task<IReadOnlyList<string>> GetLinkedTitlesAsync(string title, int limit = 10, CancellationToken ct = default)
    {
        var url = Build(new()
        {
            ["action"] = "query",
            ["prop"] = "links",
            ["plnamespace"] = "0",
            ["pllimit"] = limit.ToString(),
            ["redirects"] = "1",
            ["titles"] = title,
            ["format"] = "json"
        });

        using var doc = await GetJsonAsync(url, ct);
        var page = FirstPage(doc);
        if (page is null || !page.Value.TryGetProperty("links", out var links))
            return [];

        var titles = new List<string>();
        foreach (var link in links.EnumerateArray())
            if (link.TryGetProperty("title", out var t) && t.GetString() is { } linked)
                titles.Add(linked);
        return titles;
    }

    /// <summary>Returns external URLs cited by the article (its reference links).</summary>
    public async Task<IReadOnlyList<string>> GetExternalLinksAsync(string title, int limit = 40, CancellationToken ct = default)
    {
        var url = Build(new()
        {
            ["action"] = "query",
            ["prop"] = "extlinks",
            ["ellimit"] = limit.ToString(),
            ["redirects"] = "1",
            ["titles"] = title,
            ["format"] = "json"
        });

        using var doc = await GetJsonAsync(url, ct);
        var page = FirstPage(doc);
        if (page is null || !page.Value.TryGetProperty("extlinks", out var extlinks))
            return [];

        var urls = new List<string>();
        foreach (var link in extlinks.EnumerateArray())
        {
            // MediaWiki returns either {"url": "..."} or {"*": "..."} depending on version.
            string? value = link.TryGetProperty("url", out var u) ? u.GetString()
                : link.TryGetProperty("*", out var star) ? star.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(value))
                urls.Add(value!);
        }
        return urls;
    }

    private string Build(Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{ApiEndpoint}?{query}";
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // The Action API nests results under query.pages.{pageid}; return the first page element.
    private static JsonElement? FirstPage(JsonDocument? doc)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("query", out var query)
            || !query.TryGetProperty("pages", out var pages))
            return null;

        foreach (var page in pages.EnumerateObject())
            return page.Value;
        return null;
    }
}
