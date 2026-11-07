// Copyright (c) 2026 — LessonForge. Original work.

namespace LessonForge.Core.Rag;

/// <summary>
/// A documented, scoped decision about source processing. Source license, automated
/// access conditions and AI-processing restrictions are distinct questions; all
/// permissions default to false. A URL being publicly accessible grants none of them.
/// This is not an automated legal opinion.
/// </summary>
public sealed record SourceRights
{
    public string LicenseId { get; init; } = "";
    public string PolicyUrl { get; init; } = "";
    public DateOnly ReviewedOn { get; init; }
    public string Scope { get; init; } = "";
    public string Attribution { get; init; } = "";

    public bool MayRetrieve { get; init; }
    public bool MayRetain { get; init; }
    public bool MayUseInAi { get; init; }
    public bool MayReproduceExcerpts { get; init; }
    public bool MayRedistribute { get; init; }
    public bool AutomatedAccessPermitted { get; init; }
    public bool ExplicitAiProhibition { get; init; }
}

public sealed record SourcePermissionDecision(
    bool Allowed, string Reason, SourceRights? Rights);

/// <summary>
/// A deterministic, pre-embedding guard. This cannot certify a copyright claim;
/// it enforces that trusted acquisition code records an explicit, scoped permission
/// decision rather than asking an LLM to infer legal eligibility.
/// </summary>
public static class SourcePermissionGate
{
    public static SourcePermissionDecision Evaluate(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rights = document.Rights;
        if (rights is null)
            return Deny("Missing item-level rights review.");
        if (string.IsNullOrWhiteSpace(document.ProviderId) ||
            string.IsNullOrWhiteSpace(rights.LicenseId) ||
            string.IsNullOrWhiteSpace(rights.Scope) ||
            rights.ReviewedOn == default ||
            !Uri.TryCreate(rights.PolicyUrl, UriKind.Absolute, out var policy) ||
            policy.Scheme != Uri.UriSchemeHttps)
            return Deny("Incomplete provider, license, review scope or policy evidence.");
        if (rights.ReviewedOn > DateOnly.FromDateTime(DateTime.UtcNow))
            return Deny("Rights review date is in the future.");
        if (rights.ExplicitAiProhibition)
            return Deny("Explicit AI processing restriction.");
        if (!rights.AutomatedAccessPermitted || !rights.MayRetrieve ||
            !rights.MayRetain || !rights.MayUseInAi)
            return Deny("Required automated retrieval, retention or AI permission is not documented.");
        if (!Uri.TryCreate(document.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return Deny("Source URL is not a secure absolute URL.");
        if (string.IsNullOrWhiteSpace(document.Text) || document.Text.Length > 150_000)
            return Deny("Source text is empty or exceeds the ingestion size limit.");

        // Rights claims are scoped to known extraction routes, not whole domains.
        if (document.ProviderId == "wikipedia-text" &&
            (document.Kind != SourceKind.WikipediaArticle ||
             !uri.Host.Equals("en.wikipedia.org", StringComparison.OrdinalIgnoreCase) ||
             rights.LicenseId != "CC-BY-SA-4.0"))
            return Deny("Wikipedia license applies only to encyclopedia article text.");
        if (document.ProviderId == "wikibooks-text" &&
            (document.Kind != SourceKind.WikibooksPage ||
             !uri.Host.Equals("en.wikibooks.org", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal) ||
             uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
             !string.Equals(
                 Uri.UnescapeDataString(uri.AbsolutePath["/wiki/".Length..]).Replace('_', ' '),
                 document.CanonicalId, StringComparison.Ordinal) ||
             document.CanonicalId.Contains(':') &&
                 (document.CanonicalId.StartsWith("Wikibooks:", StringComparison.OrdinalIgnoreCase) ||
                  document.CanonicalId.StartsWith("User:", StringComparison.OrdinalIgnoreCase) ||
                  document.CanonicalId.StartsWith("Talk:", StringComparison.OrdinalIgnoreCase) ||
                  document.CanonicalId.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) ||
             rights.LicenseId != "CC-BY-SA-4.0" ||
             rights.PolicyUrl != "https://en.wikibooks.org/wiki/Wikibooks:Copyrights"))
            return Deny("Wikibooks permission applies only to official instructional page text from the MediaWiki API.");
        if (document.ProviderId == "usgs-earthquakes" &&
            (document.Kind != SourceKind.UsgsEarthquake ||
             !uri.Host.Equals("earthquake.usgs.gov", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/earthquakes/eventpage/", StringComparison.Ordinal) ||
             rights.LicenseId != "US-PD"))
            return Deny("USGS decision applies only to structured earthquake event data.");
        if (document.ProviderId == "loc-century-of-lawmaking" &&
            (document.Kind != SourceKind.LocCongressionalCatalog ||
             !uri.Host.Equals("www.loc.gov", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/item/", StringComparison.Ordinal) ||
             rights.LicenseId != "US-PD" ||
             rights.PolicyUrl !=
                "https://www.loc.gov/collections/century-of-lawmaking/about-this-collection/rights-and-access/"))
            return Deny("LoC decision is restricted to approved public-domain congressional catalog records.");
        if (document.ProviderId == "gutenberg-catalog" &&
            (document.Kind != SourceKind.GutenbergCatalog ||
             !uri.Host.Equals("www.gutenberg.org", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/ebooks/", StringComparison.Ordinal) ||
             !int.TryParse(uri.AbsolutePath["/ebooks/".Length..], out var bookId) ||
             bookId is < 1 or > 9_999_999 ||
             document.CanonicalId != bookId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
             uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
             rights.LicenseId != "PG-CATALOG-PD" ||
             rights.PolicyUrl != "https://www.gutenberg.org/policy/robot_access.html"))
            return Deny("Gutenberg rights apply only to official catalog metadata, not ebook text.");
        if (document.ProviderId == "smithsonian-openaccess-metadata" &&
            (document.Kind != SourceKind.SmithsonianMetadata ||
             !uri.Host.Equals("collections.si.edu", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/search/detail/edanmdm:", StringComparison.Ordinal) ||
             uri.AbsolutePath["/search/detail/".Length..] != document.CanonicalId ||
             uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
             rights.LicenseId != "CC0-1.0" ||
             rights.PolicyUrl != "https://www.si.edu/openaccess/faq"))
            return Deny("Smithsonian rights apply only to EDAN metadata explicitly marked CC0.");
        if (document.ProviderId is not ("wikipedia-text" or "wikibooks-text" or "usgs-earthquakes" or "loc-century-of-lawmaking" or "gutenberg-catalog" or "smithsonian-openaccess-metadata"))
            return Deny("This acquisition route has not been approved for AI ingestion.");

        return new SourcePermissionDecision(true, "Scoped rights review permits model input.", rights);

        SourcePermissionDecision Deny(string reason) => new(false, reason, rights);
    }

    public static void RequireAiEligible(SourceDocument document)
    {
        var decision = Evaluate(document);
        if (!decision.Allowed)
            throw new InvalidOperationException(
                $"Source permission denied for provider '{document.ProviderId}': {decision.Reason}");
    }
}

/// <summary>
/// Narrow, reviewed permissions for the two exact acquisition paths used in Phase 1.
/// Do not apply these to embedded third-party materials, arbitrary URLs or media.
/// </summary>
public static class ReviewedSourceRights
{
    public static SourceRights WikipediaArticleText => new()
    {
        LicenseId = "CC-BY-SA-4.0",
        PolicyUrl = "https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use",
        ReviewedOn = new DateOnly(2026, 11, 6),
        Scope = "English Wikipedia Action API plaintext article extracts, excluding media, third-party references and special rights notices",
        Attribution = "Wikipedia contributors; link to original article and CC BY-SA 4.0 license",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        // Derivative-work and share-alike obligations require additional review.
        MayReproduceExcerpts = false,
        MayRedistribute = false,
        AutomatedAccessPermitted = true
    };

    public static SourceRights WikibooksPageText => new()
    {
        LicenseId = "CC-BY-SA-4.0",
        PolicyUrl = "https://en.wikibooks.org/wiki/Wikibooks:Copyrights",
        ReviewedOn = new DateOnly(2026, 11, 7),
        Scope = "Only English Wikibooks instructional namespace-0 chapter paragraphs/headings via the official MediaWiki Action API, excluding external references, images, tables, transcluded media and wiki administration pages",
        Attribution = "Wikibooks contributors; link to original chapter and CC BY-SA 4.0 license",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        MayReproduceExcerpts = false,
        MayRedistribute = false,
        AutomatedAccessPermitted = true
    };

    public static SourceRights SmithsonianCc0Metadata => new()
    {
        LicenseId = "CC0-1.0",
        PolicyUrl = "https://www.si.edu/openaccess/faq",
        ReviewedOn = new DateOnly(2026, 11, 6),
        Scope = "Only Smithsonian Open Access public AWS EDAN JSONL records carrying content.descriptiveNonRepeating.metadata_usage.access=CC0; only selected catalog title and freetext metadata, no media, photos, object files or non-CC0 assets",
        Attribution = "Smithsonian Institution; associated contributing museum and object catalog URL (not endorsement)",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        MayReproduceExcerpts = true,
        MayRedistribute = true,
        AutomatedAccessPermitted = true
    };

    public static SourceRights GutenbergCatalogMetadata => new()
    {
        LicenseId = "PG-CATALOG-PD",
        PolicyUrl = "https://www.gutenberg.org/policy/robot_access.html",
        ReviewedOn = new DateOnly(2026, 11, 6),
        Scope = "Only the official Project Gutenberg pg_catalog.csv.gz machine-readable catalog metadata; no ebook text, third-party images, copyright-restricted works or use of the trademark as product branding",
        Attribution = "Project Gutenberg catalog; original ebook landing page and listed author",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        MayReproduceExcerpts = true,
        MayRedistribute = true,
        AutomatedAccessPermitted = true
    };

    public static SourceRights LocCenturyOfLawmakingCatalog => new()
    {
        LicenseId = "US-PD",
        PolicyUrl = "https://www.loc.gov/collections/century-of-lawmaking/about-this-collection/rights-and-access/",
        ReviewedOn = new DateOnly(2026, 11, 6),
        Scope = "Library of Congress A Century of Lawmaking for a New Nation (1774–1875) official JSON API catalog records only; no images, OCR, third-party content or unreviewed LoC collections",
        Attribution = "Law Library of Congress; original item title and canonical record URL",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        MayReproduceExcerpts = true,
        MayRedistribute = true,
        AutomatedAccessPermitted = true
    };

    public static SourceRights UsgsEarthquakeEvents => new()
    {
        LicenseId = "US-PD",
        PolicyUrl = "https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits",
        ReviewedOn = new DateOnly(2026, 11, 6),
        Scope = "USGS Earthquake Hazards Program structured GeoJSON event observations only; no media, embedded reports or third-party products",
        Attribution = "U.S. Geological Survey (USGS)",
        MayRetrieve = true,
        MayRetain = true,
        MayUseInAi = true,
        MayReproduceExcerpts = true,
        MayRedistribute = true,
        AutomatedAccessPermitted = true
    };
}
