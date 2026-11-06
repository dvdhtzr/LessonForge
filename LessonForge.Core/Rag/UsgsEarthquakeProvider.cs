// Copyright (c) 2026 — LessonForge. Original work.

using System.Globalization;
using System.Text.Json;

namespace LessonForge.Core.Rag;

/// <summary>
/// First additional real evidence provider: recent USGS-authored earthquake
/// observations from the official GeoJSON feed. It extracts only typed seismic
/// event fields, never third-party reports, article bodies, or image descriptions.
/// </summary>
public sealed class UsgsEarthquakeProvider(HttpClient http) : IEducationalSourceProvider
{
    public const string ProviderId = "usgs-earthquakes";
    public const string FeedUrl =
        "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/4.5_week.geojson";

    public string Id => ProviderId;
    public int MaxEvents { get; init; } = 3;
    public int MaxResponseBytes { get; init; } = 1_500_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(12);

    public bool SupportsTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        (topic.Contains("earthquake", StringComparison.OrdinalIgnoreCase) ||
         topic.Contains("seismology", StringComparison.OrdinalIgnoreCase) ||
         topic.Contains("seismic event", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SourceDocument>> RetrieveAsync(
        string topic, CancellationToken ct = default)
    {
        if (!SupportsTopic(topic)) return [];
        if (MaxEvents is < 1 or > 10 || MaxResponseBytes is < 1024 or > 3_000_000 ||
            Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("USGS source limits are invalid.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        using var response = await http.GetAsync(
            FeedUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode ||
            response.RequestMessage?.RequestUri is not { } effective ||
            !effective.Host.Equals("earthquake.usgs.gov", StringComparison.OrdinalIgnoreCase) ||
            effective.Scheme != Uri.UriSchemeHttps ||
            response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/geo+json"))
            return [];

        if (response.Content.Headers.ContentLength is > 1_500_000 ||
            response.Content.Headers.ContentLength > MaxResponseBytes)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384];
        int count;
        while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                return [];
            buffer.Write(bytes, 0, count);
        }

        using var json = JsonDocument.Parse(buffer.ToArray());
        if (!json.RootElement.TryGetProperty("type", out var collectionType) ||
            collectionType.GetString() != "FeatureCollection" ||
            !json.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
            return [];

        var sources = new List<SourceDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The feed is already newest-first. Capture a small number of concrete,
        // dated event examples, not a generalized claim about how earthquakes work.
        foreach (var feature in features.EnumerateArray())
        {
            if (sources.Count >= MaxEvents) break;
            if (!feature.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
                continue;
            var id = idElement.GetString() ?? "";
            if (id.Length is < 2 or > 80 ||
                id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
                !seen.Add(id))
                continue;

            if (!feature.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty("type", out var eventType) ||
                eventType.GetString() != "earthquake" ||
                !properties.TryGetProperty("mag", out var magnitude) ||
                magnitude.ValueKind != JsonValueKind.Number ||
                !magnitude.TryGetDouble(out var mag) || !double.IsFinite(mag) ||
                !properties.TryGetProperty("time", out var time) ||
                !time.TryGetInt64(out var epoch) ||
                !properties.TryGetProperty("place", out var placeElement) ||
                placeElement.ValueKind != JsonValueKind.String)
                continue;

            var place = placeElement.GetString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(place) || place.Length > 160) continue;
            DateTimeOffset occurred;
            try { occurred = DateTimeOffset.FromUnixTimeMilliseconds(epoch); }
            catch (ArgumentOutOfRangeException) { continue; }

            var canonical = $"https://earthquake.usgs.gov/earthquakes/eventpage/{id}";
            var magText = mag.ToString("0.##", CultureInfo.InvariantCulture);
            var text = $"USGS earthquake catalog observation (a recent example, not a general rule): " +
                $"event {id} occurred on {occurred:yyyy-MM-dd} at {occurred:HH:mm:ss} UTC, " +
                $"reported magnitude {magText}, location {place}. " +
                "Earthquake magnitudes, locations and catalog details may be revised.";

            sources.Add(new SourceDocument
            {
                Title = $"USGS earthquake {id} — M {magText} at {place}",
                Url = canonical,
                CanonicalId = id,
                ProviderId = ProviderId,
                Rights = ReviewedSourceRights.UsgsEarthquakeEvents,
                RetrievedAtUtc = DateTimeOffset.UtcNow,
                Text = text,
                Kind = SourceKind.UsgsEarthquake
            });
        }

        return sources;
    }
}
