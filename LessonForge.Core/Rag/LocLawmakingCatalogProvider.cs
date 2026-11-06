// Copyright (c) 2026 — LessonForge. Original work.

using System.Text;
using System.Text.Json;

namespace LessonForge.Core.Rag;

/// <summary>
/// Bounded, metadata-only discovery from the Library of Congress' public-domain
/// "A Century of Lawmaking for a New Nation" collection (1774–1875).
/// Catalog descriptions are evidence ABOUT a document, not its primary-source text.
/// No arbitrary item pages, OCR text, images, or third-party attachments are fetched.
/// </summary>
public sealed class LocLawmakingCatalogProvider(HttpClient http) : IEducationalSourceProvider
{
    public const string ProviderId = "loc-century-of-lawmaking";
    public const string ApiBase = "https://www.loc.gov/collections/century-of-lawmaking/";
    public const string ItemBase = "https://www.loc.gov/item/";

    private static readonly string[] s_topicHints =
    [
        "congress", "constitution", "constitutional", "founding", "independence",
        "revolutionary war", "american revolution", "federalist", "slavery",
        "abolition", "civil war", "emancipation", "reconstruction",
        "bill of rights", "legislation", "united states history", "american history",
        "house of representatives", "senate", "continental congress"
    ];

    public string Id => ProviderId;
    public int MaxResults { get; init; } = 3;
    public int MaxResponseBytes { get; init; } = 300_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(12);

    public bool SupportsTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        s_topicHints.Any(term => topic.Contains(term, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
        string topic, CancellationToken ct = default)
    {
        if (!SupportsTopic(topic)) return [];
        if (MaxResults is < 1 or > 8 || MaxResponseBytes is < 2048 or > 1_000_000 ||
            Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Library of Congress source limits are invalid.");

        // The query can only influence q; it cannot select another endpoint,
        // change API filters or cause arbitrary URL retrieval.
        var url = $"{ApiBase}?fo=json&at=results&c={MaxResults * 4}&q={Uri.EscapeDataString(topic.Trim())}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var response = await http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode ||
            response.RequestMessage?.RequestUri is not { } effective ||
            effective.Scheme != Uri.UriSchemeHttps ||
            !effective.Host.Equals("www.loc.gov", StringComparison.OrdinalIgnoreCase) ||
            effective.AbsolutePath != "/collections/century-of-lawmaking/" ||
            response.Content.Headers.ContentType?.MediaType is not ("application/json" or "text/json"))
            return [];

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return [];
            buffer.Write(bytes, 0, read);
        }

        using var json = JsonDocument.Parse(buffer.ToArray());
        // LoC's attribute filtering can expose a results wrapper or a bare
        // results array. Handle only these documented collection-list shapes.
        JsonElement results;
        if (json.RootElement.ValueKind == JsonValueKind.Array)
            results = json.RootElement;
        else if (json.RootElement.ValueKind == JsonValueKind.Object &&
                 json.RootElement.TryGetProperty("results", out var wrapped) &&
                 wrapped.ValueKind == JsonValueKind.Array)
            results = wrapped;
        else
            return [];

        var documents = new List<SourceDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results.EnumerateArray())
        {
            if (documents.Count >= MaxResults) break;
            if (item.ValueKind != JsonValueKind.Object ||
                !IsInApprovedCollection(item) ||
                HasRightsOrAccessConflict(item))
                continue;

            var rawId = GetString(item, "id");
            if (rawId is null ||
                !Uri.TryCreate(rawId, UriKind.Absolute, out var sourceUri) ||
                sourceUri.Host != "www.loc.gov" ||
                sourceUri.Scheme is not ("http" or "https") ||
                !sourceUri.AbsolutePath.StartsWith("/item/", StringComparison.Ordinal) ||
                sourceUri.Query.Length > 0 ||
                sourceUri.Fragment.Length > 0)
                continue;

            var itemId = sourceUri.AbsolutePath["/item/".Length..].Trim('/');
            if (itemId.Length is < 3 or > 100 ||
                itemId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                continue;
            var canonical = $"{ItemBase}{itemId}/";
            if (!seen.Add(canonical))
                continue;

            var title = Clean(GetString(item, "title"), 180);
            var description = Clean(GetFirstText(item, "description"), 520);
            var summary = Clean(GetFirstText(item, "summary"), 300);
            var date = Clean(GetString(item, "date") ?? GetString(item, "date_issued"), 60);
            if (title.Length < 6 || (description.Length < 35 && summary.Length < 35))
                continue;
            if (summary == description) summary = "";

            // Model sees only bibliographic facts and staff catalog descriptions,
            // explicitly labelled as such. Do not infer that the primary document
            // itself has been read or that the item supports broader claims.
            var text = new StringBuilder();
            text.Append("Library of Congress catalog metadata about a public-domain ");
            text.Append("congressional historical document, NOT the document's full text. ");
            text.Append("Record title: ").Append(title).Append(". ");
            if (date.Length > 0)
                text.Append("Catalog date: ").Append(date).Append(". ");
            if (description.Length > 0)
                text.Append("Catalog description: ").Append(description).Append(". ");
            if (summary.Length > 0)
                text.Append("Catalog summary: ").Append(summary).Append(". ");
            text.Append("This record describes the historical source; it does not verify ");
            text.Append("all claims that could be made about the document.");

            documents.Add(new SourceDocument
            {
                Title = $"Library of Congress catalog: {title}",
                Url = canonical,
                CanonicalId = itemId,
                Kind = SourceKind.LocCongressionalCatalog,
                ProviderId = ProviderId,
                Rights = ReviewedSourceRights.LocCenturyOfLawmakingCatalog,
                Text = text.ToString(),
                RetrievedAtUtc = DateTimeOffset.UtcNow
            });
        }

        return documents;
    }

    private static bool IsInApprovedCollection(JsonElement item)
    {
        if (!item.TryGetProperty("partof", out var partOf) ||
            partOf.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var v in partOf.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.String &&
                v.GetString()?.Contains("A Century of Lawmaking for a New Nation",
                    StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    private static bool HasRightsOrAccessConflict(JsonElement item)
    {
        if (item.TryGetProperty("access_restricted", out var restricted) &&
            restricted.ValueKind == JsonValueKind.True)
            return true;

        foreach (var field in new[] { "rights_advisory", "rights_information", "access_advisory", "rights" })
        {
            if (!item.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                return true;
            if (value.ValueKind == JsonValueKind.Array &&
                value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                return true;
            var statements = value.ValueKind == JsonValueKind.String
                ? [value.GetString() ?? ""]
                : value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (statements.Any(s =>
                s.Contains("permission required", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("rights reserved", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("copyrighted", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("restricted", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("not evaluated", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetFirstText(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var v in value.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
        return null;
    }

    private static string Clean(string? input, int limit)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var normalized = string.Join(' ', input.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var cleaned = new string(normalized
            .Select(c => c is '<' or '>' or '{' or '}' ? ' ' : char.IsControl(c) ? ' ' : c)
            .ToArray()).Trim();
        return cleaned.Length <= limit ? cleaned : cleaned[..limit].TrimEnd() + "…";
    }
}
