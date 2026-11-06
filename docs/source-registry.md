# Open Educational Sources — architecture and policy review
**Reviewed:** 2026-11-06  
**Status:** Wikipedia + USGS vertical slice locally verified; Library of Congress archival-catalog adapter implemented, pending Windows verification and live LoC API smoke test.

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
Wikipedia plaintext     USGS earthquake JSON     LoC Century of Lawmaking API
     | CC BY-SA text      | typed events only       | reviewed PD catalog only
     +--------------------+-------------------------+
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
configured adapters execute. The main application configures USGS and a
narrow Library of Congress collection adapter without any API key.
Neither provider crawls arbitrary linked websites, retrieves attachments
or treats another collection's rights statement as reusable permission. A live provider outage is isolated; a run without **any**
rights-eligible evidence fails rather than claiming successful research.

## Per-item decisions

A source's rights record has separate booleans for retrieval, retention,
AI-processing, excerpt reproduction, and redistribution, plus a separate
automated-access permission, explicit AI prohibition, license ID, policy URL,
scope, attribution text and review date.

**Default is deny.** `SourcePermissionGate` permits the model-input path only
for verified, scoped source types and providers. A recognized provider's domain
alone does not grant rights: the Wikipedia decision is limited to MediaWiki
article plaintext, the USGS decision to structured catalog event observations,
and the LoC decision to bibliographic descriptions in one explicitly
public-domain collection. None of these policies cover every linked asset.

These application decisions are bounded engineering controls, **not legal
opinions**. Recheck vendor terms and jurisdiction-specific rights before new
uses, especially output redistribution and commercial publication.

### Candidate status

| Provider | Access method | Rollout | Risk/next decision |
|---|---|---|---|
| Wikipedia (English article plaintext) | MediaWiki Action API | Enabled | CC BY-SA attribution/ShareAlike; imported third-party and non-text material may have exceptions |
| USGS Earthquake Hazards GeoJSON | Official 4.5+ weekly feed | Enabled for earthquake topics only | USGS-curated structured observations; no linked event products or media |
| Smithsonian Open Access — public AWS EDAN metadata | Anonymous official S3 JSONL shards, scoped by museum unit | Enabled for **CC0 metadata records only** | Up to eight sampled shards (not comprehensive); media rights are separate, no images/3D/audio or key-required API used |
| Library of Congress — *A Century of Lawmaking for a New Nation* | Official JSON API | Enabled for U.S. constitutional and congressional-history topics | Only explicitly identified records in the public-domain collection; no OCR, book contents, linked assets or conflicting item rights |
| Project Gutenberg weekly catalog CSV | Official machine-readable CSV, locally cached | Enabled for bibliographic metadata only | No full-text ebook retrieval or implicit edition copyright clearance |
| Saylor University | Original course materials and links | Disabled | Original CC BY content differs from third-party sources and exam banks |

References for policy review:
- Wikimedia Foundation: https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use
- Smithsonian Open Access developer tools: https://www.si.edu/openaccess/devtools
- Smithsonian Open Access FAQ: https://www.si.edu/openaccess/faq
- Public Smithsonian Open Access AWS dataset: https://registry.opendata.aws/smithsonian-open-access/
- Smithsonian public bucket metadata index: https://smithsonian-open-access.s3-us-west-2.amazonaws.com/metadata/edan/index.txt
- Smithsonian EDAN metadata field guidance: https://edan.si.edu/openaccess/docs/
- Project Gutenberg official offline catalog: https://www.gutenberg.org/ebooks/offline_catalogs.html
- Smithsonian API: https://www.si.edu/openaccess/devtools
- Smithsonian Terms: https://www.si.edu/termsofuse
- USGS copyright: https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits
- USGS GeoJSON format: https://earthquake.usgs.gov/earthquakes/feed/v1.0/geojson.php
- USGS feed: https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/4.5_week.geojson
- Library of Congress API: https://www.loc.gov/apis/json-and-yaml
- LoC Century of Lawmaking collection: https://www.loc.gov/collections/century-of-lawmaking/
- LoC collection public-domain rights: https://www.loc.gov/collections/century-of-lawmaking/about-this-collection/rights-and-access/
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
- **Library of Congress:** one explicitly public-domain congressional history
  collection (1774–1875), official JSON catalog API, topic-scoped search,
  bounded results/response/timeout, HTTPS final-endpoint check, verified
  collection membership and no conflicting item rights. Only title, date
  and bibliographic descriptions are normalized as *catalog metadata*,
  never OCR, underlying primary documents, images or arbitrary item links.
  Such descriptions support facts about a document's existence/metadata,
  **not historical assertions drawn from its unseen full text**.
- **Project Gutenberg:** locally caches the *official* machine-readable
  `pg_catalog.csv` once every seven days; bounded download (35 MB), CSV
  quoting support, topic matching, canonical ebook record links. Only book
  titles/authors/subject headings and Gutenberg release dates are admitted.
  No ebook contents are read; the edition, territorial rights, and original
  source publication dates require independent verification.
- **Smithsonian:** the key-required API is not used. An anonymous, official
  Open Access S3 bucket provides publicly indexed JSONL shards; this pilot
  inspects up to eight bounded hash-shard files from one unit selected by
  subject (NMAH, SAAM, NASM, NMNH Botany). It admits only `edanmdm` items
  where `content.descriptiveNonRepeating.metadata_usage.access == "CC0"`,
  and normalizes catalog title/date/names/selected object notes only.
  Media rights are independent of metadata rights. No media, object images,
  3D assets, restricted texts or non-CC0 metadata enters RAG. Because shard
  selection samples only a subset of the catalog, a valid source may not be
  discovered; that must never produce invented citations.
- **Provenance:** trusted records include source provider ID, canonical document
  ID/URL, retrieval timestamp, license/scope/policy, and SHA-256 of the text
  actually indexed. Chunks copy source identity and content hash.
- **Citations:** Research/Planner/Quiz stage boundaries reject unknown URLs and
  normalize titles from the retrieved index. This proves a source was in the
  licensed corpus; it does **not** prove a particular claim is supported by a
  particular passage. Passage identifiers and excerpt-level factual-support
  review remain future work.
- **Perspective review validation:** a model-proposed disagreement is accepted
  only when two viewpoints have all required nonempty fields and citations
  from retrieved rights-eligible sources. Missing data (including a partially
  populated second viewpoint) triggers at most one regenerated comparison.
  If repeated results fail deterministic evidence/field validation, the
  educator receives explicit `insufficient_evidence` with no viewpoints,
  explaining the failed automated review rather than claiming there is or
  isn't a scholarly debate. Transport and malformed JSON errors remain fatal.
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
6. **Live LoC acceptance:** run a lesson on *The United States Constitution* or
   *Congress and the Civil War*, and inspect whether any LoC catalog results
   were admitted. The public API may return no eligible matches: that must not
   break Wikipedia research or cause invented LoC citations.
7. **Future: additional provider:** Smithsonian per-item CC0 metadata after
   confirming API terms and rights on the exact material to ingest.

The preceding Wikipedia/USGS implementation passed the user's local gate and
functional smoke check. The new LoC adapter and its offline fixtures are
committed, but **its local .NET verification and live API/Ollama integration
are not yet confirmed**. The LoC API could return zero records under the
strict collection membership/rights filters; inspect live evidence inventory
before treating this provider as accepted.

## Smithsonian live smoke and limitations

Test **American Revolution**, **early aviation** or **American landscape
painting** using the new metadata adapter. Inspect the Research source
inventory and teacher `source_records` for
`smithsonian-openaccess-metadata`. If eligible records appear, verify
their canonical `collections.si.edu/search/detail/edanmdm:...` links,
and confirm the generated lesson does not claim to have read the museum
object or seen its media. For unrelated topics or records without
CC0 metadata flags, missing Smithsonian sources are an acceptable,
explicit outcome; do not loosen the gate.

The Smithsonian dataset is large and the eight-shard search is a
**bounded sample, not a full-corpus index**. Later work may add a locally
built index over approved bulk metadata, but should not blindly download
or embed millions of records per request. No paid services, API keys,
AWS account or managed inference are required.

These PR additions are committed as offline-testable code, not yet
verified in a running .NET 10/Ollama environment. The owner's local
`scripts/check.ps1` and manual live source inventory remain the
acceptance criteria.
