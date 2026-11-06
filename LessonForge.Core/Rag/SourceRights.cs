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
        if (document.ProviderId == "usgs-earthquakes" &&
            (document.Kind != SourceKind.UsgsEarthquake ||
             !uri.Host.Equals("earthquake.usgs.gov", StringComparison.OrdinalIgnoreCase) ||
             !uri.AbsolutePath.StartsWith("/earthquakes/eventpage/", StringComparison.Ordinal) ||
             rights.LicenseId != "US-PD"))
            return Deny("USGS decision applies only to structured earthquake event data.");
        if (document.ProviderId is not ("wikipedia-text" or "usgs-earthquakes"))
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
