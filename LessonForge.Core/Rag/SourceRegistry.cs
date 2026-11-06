// Copyright (c) 2026 — LessonForge. Original work.

namespace LessonForge.Core.Rag;

public sealed record EducationalSourceDescriptor(
    string Id,
    string DisplayName,
    string Homepage,
    string Acquisition,
    string Subjects,
    string PolicyUrl,
    bool Enabled,
    string PermissionStatus);

/// <summary>Provider adapters return narrowly scoped, normalized candidate documents.</summary>
public interface IEducationalSourceProvider
{
    string Id { get; }
    bool SupportsTopic(string topic);
    Task<IReadOnlyList<SourceDocument>> RetrieveAsync(string topic, CancellationToken ct = default);
}

/// <summary>
/// Curated catalog, not an open-web scraper. Discovery uses only explicitly enabled
/// adapters; each returned item must still independently pass SourcePermissionGate.
/// </summary>
public sealed class SourceRegistry
{
    private readonly IReadOnlyList<IEducationalSourceProvider> _adapters;

    public IReadOnlyList<EducationalSourceDescriptor> Sources { get; }

    public SourceRegistry(IEnumerable<IEducationalSourceProvider>? adapters = null)
    {
        _adapters = (adapters ?? []).ToArray();
        if (_adapters.Any(a => a is null) ||
            _adapters.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != _adapters.Count)
            throw new ArgumentException("Source adapters must be nonnull with unique IDs.", nameof(adapters));
        if (_adapters.Any(a => a.Id is not ("usgs-earthquakes" or "loc-century-of-lawmaking" or "gutenberg-catalog" or "smithsonian-openaccess-metadata")))
            throw new ArgumentException("Only explicitly reviewed USGS, LoC, Gutenberg and Smithsonian metadata adapters are enabled.", nameof(adapters));

        Sources =
        [
            new("wikipedia-text", "English Wikipedia", "https://en.wikipedia.org",
                "MediaWiki Action API", "General reference",
                "https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use",
                true, "Scoped CC BY-SA article-text AI input; external references require separate review"),
            new("usgs-earthquakes", "USGS Earthquake Hazards", "https://earthquake.usgs.gov",
                "Official GeoJSON feed", "Seismology / Earth science",
                "https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits",
                _adapters.Any(a => a.Id == "usgs-earthquakes"),
                "Scoped USGS-authored observations permitted; no third-party media"),
            new("smithsonian-openaccess-metadata", "Smithsonian Open Access — CC0 Catalog Metadata", "https://www.si.edu/openaccess",
                "Public anonymous AWS JSONL metadata shards with per-record CC0 check", "Art, American history, aerospace, natural history",
                "https://www.si.edu/openaccess/faq",
                _adapters.Any(a => a.Id == "smithsonian-openaccess-metadata"),
                "CC0 EDAN catalog metadata only; does not download media or use the key-required Open Access API"),
            new("loc-century-of-lawmaking", "Library of Congress — A Century of Lawmaking", "https://www.loc.gov/collections/century-of-lawmaking/",
                "Official collection JSON API; descriptive records only", "U.S. history, constitutional and congressional documents (1774–1875)",
                "https://www.loc.gov/collections/century-of-lawmaking/about-this-collection/rights-and-access/",
                _adapters.Any(a => a.Id == "loc-century-of-lawmaking"),
                "Public-domain collection catalog descriptions only; no OCR, images, unreviewed collections or conflicting item rights"),
            new("gutenberg-catalog", "Project Gutenberg — Official Catalog", "https://www.gutenberg.org",
                "Official weekly CSV.gz bulk catalog; locally cached", "Literature, historical books, philosophy and primary-source bibliographies",
                "https://www.gutenberg.org/policy/robot_access.html",
                _adapters.Any(a => a.Id == "gutenberg-catalog"),
                "Public-domain CATALOG METADATA only; ebook text requires separate item rights and mirror-access review"),
            new("saylor", "Saylor University", "https://www.saylor.org",
                "Course metadata / original materials", "University-level education",
                "https://www.saylor.org/OpenContent", false,
                "Original-vs-third-party and assessment rights unresolved")
        ];
    }

    public async Task<IReadOnlyList<SourceDocument>> RetrieveEligibleAsync(
        string topic, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var documents = new List<SourceDocument>();
        foreach (var adapter in _adapters.Where(a => a.SupportsTopic(topic)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Checking reviewed source {adapter.Id}…");
            try
            {
                var candidates = await adapter.RetrieveAsync(topic, ct);
                if (candidates.Count == 0)
                    progress?.Report($"No eligible results from {adapter.Id} for this query.");
                foreach (var source in candidates)
                {
                    if (source is null || source.ProviderId != adapter.Id)
                    {
                        progress?.Report($"Skipped malformed source from {adapter.Id}.");
                        continue;
                    }

                    var permission = SourcePermissionGate.Evaluate(source);
                    if (!permission.Allowed)
                    {
                        progress?.Report($"Skipped {adapter.Id} source: {permission.Reason}");
                        continue;
                    }
                    documents.Add(source);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // One optional source outage must not prevent licensed Wikipedia
                // research, and no remote response content is echoed in diagnostics.
                progress?.Report($"Optional source {adapter.Id} unavailable; continuing.");
            }
        }
        return documents;
    }
}
