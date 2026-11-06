// Copyright (c) 2026 — LessonForge. Original work.

using System.Net;
using System.Text;
using HtmlAgilityPack;
using UglyToad.PdfPig;

namespace LessonForge.Core.Rag;

/// <summary>
/// Fetches freely-retrievable external reference URLs and extracts readable plain text (HTML via
/// HtmlAgilityPack, PDF via PdfPig). Everything is best-effort and bounded: strict timeout, a size
/// cap, and a content-type allowlist. A URL that is paywalled, times out, is too large, or is an
/// unsupported type is simply skipped — a bad reference never fails a run.
/// </summary>
public sealed class SourceFetcher(HttpClient http)
{
    /// <summary>Hard cap on bytes read from any one reference.</summary>
    public int MaxBytes { get; init; } = 3_000_000;

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Minimum extracted characters for a reference to be worth keeping.</summary>
    public int MinUsefulChars { get; init; } = 300;

    /// <summary>
    /// Attempts to fetch and extract a single reference. Returns null when the URL can't be used
    /// (non-HTML/PDF, error status, too large, too little text, or any transport failure).
    /// </summary>
    public async Task<SourceDocument?> TryFetchAsync(string url, string? citedBy = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var bytes = await ReadCappedAsync(response, cts.Token);
            if (bytes.Length == 0)
                return null;

            var text = mediaType.Contains("pdf") || LooksLikePdf(bytes)
                ? ExtractPdf(bytes)
                : mediaType.Contains("html") || mediaType.Contains("xml") || mediaType == ""
                    ? ExtractHtml(bytes)
                    : null;

            if (text is null || text.Length < MinUsefulChars)
                return null;

            return new SourceDocument
            {
                Title = TitleFromUri(uri),
                Url = url,
                Text = text,
                Kind = SourceKind.Reference,
                CitedBy = citedBy
            };
        }
        catch
        {
            // Timeouts, DNS failures, TLS issues, malformed content — skip gracefully.
            return null;
        }
    }

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var temp = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(temp, ct)) > 0)
        {
            buffer.Write(temp, 0, read);
            if (buffer.Length > MaxBytes)
                break;
        }
        return buffer.ToArray();
    }

    private static bool LooksLikePdf(byte[] bytes)
        => bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static string? ExtractPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                sb.Append(page.Text);
                sb.Append('\n');
                if (sb.Length > 200_000) break;
            }
            return Collapse(sb.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractHtml(byte[] bytes)
    {
        try
        {
            var html = Encoding.UTF8.GetString(bytes);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var node in doc.DocumentNode
                .SelectNodes("//script|//style|//nav|//footer|//header|//noscript|//form|//aside")
                ?.ToList() ?? [])
            {
                node.Remove();
            }

            // Prefer the main article content when the page marks it; else fall back to body.
            var main = doc.DocumentNode.SelectSingleNode("//article")
                ?? doc.DocumentNode.SelectSingleNode("//main")
                ?? doc.DocumentNode.SelectSingleNode("//body")
                ?? doc.DocumentNode;

            return Collapse(WebUtility.HtmlDecode(main.InnerText));
        }
        catch
        {
            return null;
        }
    }

    // Collapse runs of whitespace into single spaces/newlines so chunks are clean.
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        var newlineRun = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                if (newlineRun < 2) sb.Append('\n');
                newlineRun++;
                lastWasSpace = true;
                continue;
            }
            newlineRun = 0;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string TitleFromUri(Uri uri)
    {
        var host = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(path) ? host : $"{host}/{path}";
    }
}
