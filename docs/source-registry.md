# Open Educational Sources — architecture and policy review
**Reviewed:** 2026-11-06  
**Status:** source-registry/permission-gate pilot; local compile and live Ollama verification pending.

## Architecture audit (before modification)

The existing solution is .NET 10 + Microsoft Agent Framework with typed
executors, ASP.NET Core, local Ollama, SQLite, React/TypeScript and QuestPDF.
`ResearchAgent` calls `RetrievalService.BuildAsync`; its Wikipedia client uses
the MediaWiki Action API for article search, article plaintext, links and
third-party reference URLs. `SourceFetcher` previously retrieved any available
HTML/PDF external reference without evaluating rights. `SourceDocument` was
split by `Chunker`, embedded into `InMemoryVectorIndex`, and handed back
through `RetrievalSession` and `RagPrompt` as citation-labelled snippets.
The Research Librarian, optional Perspective Analyst, Curriculum Designer,
Assessment Writer and Accuracy Editor use that shared per-run index.

Important gaps: no provider registry, no per-item AI-usage authorization,
no distinction between source URL and rights evidence, no persistent content
hash/retrieval timestamp, and no deterministic check that an agent's citation
URL belongs to material actually retrieved. The permission changes deliberately
leave the MAF graph, in-memory vector store and local inference unchanged.

## New narrow data path

```
Wikipedia plaintext extractor        USGS Earthquake GeoJSON adapter
    | CC BY-SA text only                 | typed event fields only
    +----------------------+--------------+
                           |
          SourceDocument + SourceRights
                           |
              SourcePermissionGate
                / allow       \ deny
               /               \
     Chunker/embeddings       never embedded
               |
         RetrievalSession
               |
   existing Research/Planning/Quiz/Review agents
               |
     citation URL verification
               |
   final lesson + persisted source_records
```

`SourceRegistry` lists reviewed and disabled candidates; only explicitly
configured adapters execute. The main application configures USGS without an
API key. The adapter does not crawl arbitrary websites or parse third-party
descriptions. A live provider outage is isolated; a run without **any**
rights-eligible evidence fails rather than claiming successful research.

## Per-item decisions

A source's rights record has separate booleans for retrieval, retention,
AI-processing, excerpt reproduction, and redistribution, plus a separate
automated-access permission, explicit AI prohibition, license ID, policy URL,
scope, attribution text and review date.

**Default is deny.** `SourcePermissionGate` permits the model-input path only
for verified, scoped source types and providers. A recognized provider's domain
alone does not grant rights: the Wikipedia decision is limited to MediaWiki
article plaintext, and the USGS decision to structured catalog event
observations. Neither policy covers every linked asset.

These application decisions are bounded engineering controls, **not legal
opinions**. Recheck vendor terms and jurisdiction-specific rights before new
uses, especially output redistribution and commercial publication.

### Candidate status

| Provider | Access method | Rollout | Risk/next decision |
|---|---|---|---|
| Wikipedia (English article plaintext) | MediaWiki Action API | Enabled | CC BY-SA attribution/ShareAlike; imported third-party and non-text material may have exceptions |
| USGS Earthquake Hazards GeoJSON | Official 4.5+ weekly feed | Enabled for earthquake topics only | USGS-curated structured observations; no linked event products or media |
| Smithsonian Open Access | Official API (key) or official JSON exports | Disabled | Check each asset's CC0 designation, third-party rights, and API conditions |
| Library of Congress | Official JSON API | Disabled | Per-item rights/advisory and primary-source content access |
| Project Gutenberg | Official catalog and approved mirror/harvest paths | Disabled | U.S. vs other countries' copyright, translations and editions, robot policy/trademark |
| Saylor University | Original course materials and links | Disabled | Original CC BY content differs from third-party sources and exam banks |

References for policy review:
- Wikimedia Foundation: https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use
- Smithsonian Open Access: https://www.si.edu/openaccess/faq
- Smithsonian API: https://www.si.edu/openaccess/devtools
- Smithsonian Terms: https://www.si.edu/termsofuse
- USGS copyright: https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits
- USGS GeoJSON format: https://earthquake.usgs.gov/earthquakes/feed/v1.0/geojson.php
- USGS feed: https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/4.5_week.geojson
- Library of Congress API: https://www.loc.gov/apis/json-and-yaml
- Project Gutenberg automated access: https://www.gutenberg.org/policy/robot_access.html
- Saylor original/third-party licensing: https://www.saylor.org/OpenContent

## Retrieval and evidence integrity

- **No arbitrary cited-page fetch:** Wikipedia's external URLs are collected
  as discovery metadata only. Prior unrestricted `SourceFetcher` HTML/PDF
  extraction is intentionally not used in this initial rights-gated flow.
  Adding external content requires a reviewed provider/adapter and per-item
  rights decision, rather than lifting the gate.
- **USGS:** HTTPS fixed endpoint, final-host check, JSON content-type allowlist,
  short timeout, 1.5 MB maximum input, bounded event count, strict event ID,
  type/date/magnitude validation, and canonical event-page URLs constructed
  locally (never trusted blindly from remote JSON).
- **Provenance:** trusted records include source provider ID, canonical document
  ID/URL, retrieval timestamp, license/scope/policy, and SHA-256 of the text
  actually indexed. Chunks copy source identity and content hash.
- **Citations:** Research/Planner/Quiz stage boundaries reject unknown URLs and
  normalize titles from the retrieved index. This proves a source was in the
  licensed corpus; it does **not** prove a particular claim is supported by a
  particular passage. Passage identifiers and excerpt-level factual-support
  review remain future work.
- **Reliability:** optional provider unavailability does not prevent Wikipedia
  research. **Primary lesson research** with no eligible documents still fails
  closed. **Supplementary perspective discovery** is allowed to return no
  additional documents; the Perspective Analyst keeps the original licensed
  research and must report insufficient evidence rather than manufacture
  disagreement. It never embeds empty/unlicensed results or invents secondary
  citations. Transport and cancellation failures are not disguised as an empty
  result. The vector index remains ephemeral; only provenance metadata and
  finished lesson artifacts persist to SQLite.
- **Source safety:** structured USGS event fields are treated as evidence data,
  not instructions. We neither ask models to interpret source terms nor allow
  source content to register tools/permissions. Wider content ingestion will
  need additional prompt-injection and network-redirect defenses.

## Acceptance and next phases

1. **Local gate:** `scripts/check.ps1` must pass in Windows, including offline
   rights, adapter, citation, retrieval, export and legacy regression tests.
2. **Live smoke, earthquake topic:** verify that indexed source inventory has
   both Wikipedia and a current USGS event page, and that cited claims use URLs
   actually retrieved. Source inventory in saved lesson should retain provider,
   rights review and hash. The Research Librarian may cite Wikipedia alone;
   *presence in the corpus is not evidence of correct use*.
3. **Live smoke, unrelated topic:** regular Wikipedia path still works and
   USGS is not called.
4. **Optional source unavailable:** Wikipedia-based lesson still completes
   when USGS is unavailable, without inventing missing USGS evidence.
5. **Future: actual passage-level support:** preserve exact excerpt/page/offset
   IDs, measure citation alignment, and improve source-independence evidence.
6. **Future: additional provider:** Smithsonian per-item CC0 metadata or
   similarly scoped, officially accessible library collection, after confirming
   API terms and rights on the exact material to ingest.

No build, network integration test or live Ollama result is asserted by this
document; offline tests are committed but require execution in the user's
Windows .NET 10 environment.
