// Copyright (c) 2026 — LessonForge. Original work.

using Microsoft.Extensions.Configuration;

namespace LessonForge.Core.Rag;

/// <summary>
/// The authoritative per-host source switches. Source IDs match provenance
/// provider IDs, preventing a second mapping from drifting out of sync.
/// Enabled here NEVER overrides SourcePermissionGate or grants AI rights.
/// </summary>
public sealed class SourceSelection
{
    public const string SectionName = "LessonForge:Sources";

    private static readonly string[] s_known =
    [
        "wikipedia-text",
        "wikibooks-text",
        "usgs-earthquakes",
        "loc-century-of-lawmaking",
        "gutenberg-catalog",
        "smithsonian-openaccess-metadata",
        "openstax-text"
    ];

    private readonly Dictionary<string, bool> _enabled;

    /// <summary>Legacy host/test defaults; the API loads explicit local switches.</summary>
    public SourceSelection(IReadOnlyDictionary<string, bool>? enabled = null)
    {
        _enabled = s_known.ToDictionary(
            id => id,
            id => id != "openstax-text",
            StringComparer.Ordinal);

        if (enabled is not null)
            foreach (var (key, value) in enabled)
            {
                if (!_enabled.ContainsKey(key))
                    throw new InvalidOperationException($"Unknown educational source '{key}'.");
                _enabled[key] = value;
            }

        // OpenStax's book pages explicitly prohibit ingestion into generative
        // AI offerings absent prior written authorization. A local toggle is
        // NOT legal authorization; fail fast before any retrieval starts.
        if (_enabled["openstax-text"])
            throw new InvalidOperationException(
                "OpenStax textbook content requires prior written permission for " +
                "generative-AI ingestion. Leave LessonForge:Sources:openstax-text=false " +
                "until a separate rights-reviewed integration is approved.");
    }

    public bool IsEnabled(string providerId)
        => _enabled.TryGetValue(providerId, out var enabled) && enabled;

    public static SourceSelection Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var entries = section.GetChildren().ToArray();

        if (entries.Length == 0)
            throw new InvalidOperationException(
                "Missing LessonForge:Sources settings. Provide appsettings.Local.json " +
                "with explicit source enablement flags.");

        var configured = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!bool.TryParse(entry.Value, out var enabled))
                throw new InvalidOperationException(
                    $"Source switch '{entry.Key}' must be true or false.");
            configured.Add(entry.Key, enabled);
        }

        foreach (var id in s_known)
            if (!configured.ContainsKey(id))
                throw new InvalidOperationException(
                    $"Required source switch '{id}' is missing from LessonForge:Sources.");

        return new SourceSelection(configured);
    }
}
