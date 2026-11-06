# LessonForge

A local-first app that turns a **topic + target audience age** into a **structured lesson plan and
a citation-backed multiple-choice quiz**, grounded in rights-eligible sources retrieved
through a local RAG index (Wikipedia articles, official USGS earthquake observations,
and selected public-domain Library of Congress congressional-history catalog records).

It is a standalone .NET 10 application: a multi-agent crew (built on the Microsoft Agent Framework)
runs against a local Ollama server, behind a C# web API, with a React frontend and an in-process
retrieval pipeline. Everything runs on your own machine — no cloud services and no API keys.

## Architecture

```
React/TS (Vite)  ──HTTP + SSE──▶  ASP.NET Core API  ──▶  MAF workflow crew  ──▶  Ollama (local)
   web/                            LessonForge.Api         LessonForge.Core          gemma4:latest
                                   SQLite (results)        RAG index (in-memory)     nomic-embed-text
```

**The crew** (MAF executors wired with typed DTOs and two human-in-the-loop review gates):

| Stage | Persona | Does |
|-------|---------|------|
| Research | Research Librarian | Retrieves Wikipedia articles, relevant USGS earthquake observations and scoped Library of Congress congressional-history catalog metadata; rights-checks and embeds eligible evidence in a per-run index |
| *optional* | **Perspective Analyst** | When enabled, retrieves additional sources, identifies substantiated differing interpretations, or explicitly reports no substantive disagreement / insufficient evidence |
| *gate* | — | Educator reviews/edits/approves grounded facts **and**, when enabled, the perspective analysis |
| Lesson Planner | Curriculum Designer | Builds an age-calibrated plan: objectives, sections, activities, vocabulary |
| *gate* | — | Educator reviews/edits/approves the plan |
| Quiz Generator | Assessment Writer | Writes MCQs (defaults: 10 questions × 4 options, difficulty auto-scaled to age) with citation-backed explanations |
| Fact-Check | Accuracy Editor | Reviews claims/answers against retrieved evidence; returns compact findings and flags without rewriting the approved lesson or quiz |

### Project Gutenberg — key-free catalog integration

The Research Librarian can discover books and source bibliographies from the
[official Project Gutenberg weekly CSV catalog](https://www.gutenberg.org/ebooks/offline_catalogs.html),
using one bounded download cached in the user's local application-data directory
for seven days. It does not scrape the public website.

**Important scope:** a Gutenberg result is **bibliographic metadata** (ebook
number, title, listed author, subjects and Project Gutenberg release date).
It is *not* the text of the book, proof of the work's contents, a statement
about the original publication date or a copyright determination for any
individual edition or translation. The separate rights gate admits this
public-domain catalog metadata, not arbitrary ebook body content. If book text
is needed, we must first design a reviewed, mirror-compatible, item-level
copyright route. Project Gutenberg's trademark and territorial restrictions
may still apply to downstream publishing.

### Smithsonian Open Access — CC0 catalog metadata

- **Smithsonian Open Access:** the [developer tools](https://www.si.edu/openaccess/devtools)
  include a key-required API and an anonymous [AWS Open Data repository](https://registry.opendata.aws/smithsonian-open-access/).
  LessonForge samples a small set of official weekly JSON-lines metadata
  shards from one relevant Smithsonian unit (such as the National Museum
  of American History, American Art Museum, or Air and Space Museum).
  **Only records explicitly marking metadata as CC0** are accepted.
  The provider does not ingest digital image files, audio, 3D media, or
  externally linked pages. The sample is not a comprehensive catalog search.
  No Smithsonian API key or AWS account is required.

These records describe the museum's catalog metadata, not the content or
meaning of an artifact or the rights to any associated image, audio or 3D
asset. The existing permission and provenance checks apply unchanged.

### Source Registry (initial rollout)

- **Reviewed paths:** Wikipedia Action API article plaintext (CC BY-SA 4.0 with
  attribution and ShareAlike considerations), USGS's official 4.5+ earthquake
  GeoJSON feed (structured event observations only), and the Library of Congress
  *A Century of Lawmaking for a New Nation* JSON catalog descriptions (1774–1875,
  public domain). Providers are only queried for relevant topics.
- **Fail-closed rights gate:** source content enters embeddings/model prompts only
  when a trusted adapter records the acquisition scope, license, policy URL,
  review date and explicit automated-retrieval/retention/AI-processing permission.
  Source legality, factual credibility, and topical relevance remain different
  concepts. Only documented source metadata and hashes are persisted, not full
  retrieved documents.
- **Changed behavior:** Wikipedia's third-party external reference URLs are still
  discovered but are **not fetched or indexed** until their individual rights are
  established. Public readability alone is not permission for AI ingestion.
- **Provenance:** the final lesson stores source provider, canonical ID and URL,
  rights scope, policy, retrieval time and SHA-256 content hash. Teacher Markdown
  and JSON exports contain this inventory. Source URLs produced by agents must
  match the material actually retrieved in the run.
- **Still excluded:** Gutenberg ebook text, Smithsonian images and other
  media assets, Saylor course material, and unreviewed Library of Congress
  collections. Each needs separate source-specific rights review.
- **Limits:** USGS provides recent earthquake examples, while LoC catalog metadata
  documents *the existence and description* of archival records—not their full
  primary-source text. Neither should be presented as direct support for broad
  historical or scientific conclusions. Citation URL
  matching checks provenance, not passage-level support. Licensing notices and
  downstream derivative-work obligations require further evaluation before
  relying on the system for commercial publication.

See [source architecture and source policy](docs/source-registry.md).

### Named-subject identity protection

An explicit named topic such as **Carmen Carrozza** must not be silently
replaced with a fuzzy Wikipedia search match such as **Jesús Carroza**.

LessonForge now probes short exact Wikipedia article titles before ranked
search results, including when a user types the title in lowercase. For
explicitly named subjects, it accepts only the matching canonical title and
does not automatically expand to other people's linked biographies. A failed
direct lookup may search for the **same** exact title, but never chooses a
similar-name Wikipedia result as a fallback. Other rights-reviewed providers
may still contribute when their records explicitly name the requested subject;
for two-part names, surname-only catalog matches are excluded.

The Research Librarian also requires named-topic summaries to identify the
requested subject. If a valid JSON response describes a different subject,
it gets **one evidence-grounded, summary-only regeneration**, preserving the
original citation-validated facts. If the replacement still names the wrong
subject, the run stops before educator approval or lesson planning. This is
a deterministic identity safeguard, not proof that every sentence or citation
is factually accurate; educator review remains essential.

### Research-summary completeness and automatic repair

The Research Librarian validates the `topic_summary` before any educator
review or lesson-planning handoff. JSON schema alone can accept an obviously
unfinished string (for example, a summary ending in "derived from the concept
of"). A conservative deterministic check rejects missing sentence-ending
punctuation, terminal ellipses and dangling conjunctions or prepositions.

If detected, LessonForge performs **one summary-only local Ollama regeneration**
using the same rights-eligible excerpts and already verified grounded facts.
It preserves the existing 8–10 cited facts, source inventory and provenance.
It does not complete a cut-off sentence by guessing or rerun the whole
multi-agent workflow. The progress stream reports that repair is underway.

If the new summary is still incomplete or model generation fails, the research
stage fails with a clear explanation rather than presenting the fragment for
educator approval. The check is heuristic: punctuation does not establish that
a sentence is factually correct, grammatically valid in every case, or
supported by the cited passages. Educator review and the Accuracy Editor remain
necessary.

### Reading layout and live agent activity

The desktop reading workspace and educator review dialogs are approximately
**20% wider** (920→1104px workspace, 660→792px general dialog, 760→912px
educator dialog). The layout remains responsive on narrow screens.

Research source links now show small, locally rendered provider/article badges
(Wikipedia, USGS, Library of Congress, Project Gutenberg and Smithsonian).
Teacher lesson pages also have a **collapsed Research source library** showing
retrieved source identity and licensing metadata. No remote logos, favicons or
article images are fetched, since those require independent image licensing and
source URL controls.

The lesson-generation page highlights the actual active agent, shows live
research/progress text, counts **completed workflow stages** (not estimated time),
and offers an expandable history of the eight most recent real SSE events.
Educator review is shown as a paused checkpoint. No synthetic agent messages,
fake progress percentages or fabricated progress animation are introduced;
a small active indicator respects reduced-motion preferences. The backend MAF
graph, review gates, SSE event contract, source permissions and model
configuration remain unchanged.

### Key design decisions

- **RAG index is in-memory and per-request** — built fresh from live Wikipedia each run, then
  discarded. Only the finished lesson/quiz is persisted (SQLite). The index is a small hand-rolled
  cosine-similarity store over the standard `IEmbeddingGenerator` abstraction, so it needs no vector
  DB and is trivially testable.
- **Models (local Ollama, configured in Git):** `gemma4:latest` for all four agents and
  `nomic-embed-text:v1.5` for the RAG embeddings. Both names are defined in the
  committed `LessonForge.Api/appsettings.json`. No API keys, cloud services, or
  manually configured environment variables are necessary.
- **Bounded Accuracy Editor review:** instead of requesting an unbounded audit of
  every section and quiz question in one model response, the final MAF executor
  validates small batches of two items, using source excerpts relevant to each
  scope. A `finish_reason=length` response is never accepted; two-item batches
  are retried as separate items, and a failed single-item review fails the
  entire stage without persisting a partial audit. Valid reports are aggregated,
  with AI-estimated citation coverage weighted by reviewed items. This uses
  additional local model calls to avoid the fragile single-response token budget.
- **Structured Accuracy Review:** the final agent produces advisory findings with
  High / Medium / Low / Informational severity, a controlled issue category,
  a lesson-section or quiz-question reference (S# / Q#), a concrete educator
  recommendation and an optional AI-written evidence summary. Severity uses a
  documented instructional rubric. C# rejects malformed categories, absent
  actions and out-of-range locations; it supplies repeatable finding IDs and only
  consolidates obvious duplicates at the same location/category/focal claim.
  The review UI groups findings and links them to relevant lesson content.
  Historical SQLite lessons with the earlier three-field flags remain readable.
  Teacher exports carry review actions; student exports do not expose flags.
  This does **not** independently prove claims true or guarantee that the review
  catches every omission, especially where the source/lesson excerpts were bounded.
- **Bounded Perspective Analyst recovery:** a supported disagreement must have
  two complete viewpoints with substantive evidence, limitations and retrieved
  citation URLs. If a local model returns a structurally plausible but
  unverifiable second viewpoint, the analysis is regenerated once with a
  smaller, stricter prompt. A second failed validation results in an
  **explicitly inconclusive** analysis with no accepted perspectives and a
  clear explanation for educator review. No missing fields, fabricated citations
  or unsupported opposing positions are silently repaired; Ollama transport,
  generation and parse failures still surface as errors.
- **Streaming + gates:** the API streams Server-Sent Events (`run` → `stage*` → `gate*` →
  `complete` → `done`). Gates map to MAF `RequestPort`s; the UI approves/edits/aborts. Active runs
  live in memory (single-user v1).
- **Evidence-aware optional analysis:** the `explore_perspectives` request toggle inserts a fifth
  MAF executor between Research and educator approval. It gathers a bounded second collection of
  Wikipedia and cited-reference sources, reports 2–3 credible interpretations *only when supported*,
  and otherwise returns an explicit no-disagreement or insufficient-evidence result. Citations must
  match documents actually retrieved; unsupported or fabricated source URLs are rejected. The
  educator approves or edits the analysis within the **existing research gate**. Approved content
  informs the plan and quiz, is reviewed for misleading representation by the Accuracy Editor,
  and is retained in the saved lesson and student/teacher exports. This is not a requirement
  to present weak claims as equal to well-supported evidence.

## Projects

- `LessonForge.Core` — config, Ollama client factory, RAG pipeline (`Rag/`), agents (`Agents/`),
  DTOs (`Dto/`), workflow (`Workflow/`).
- `LessonForge.Api` — minimal-API host: generate (SSE), gate, list/get, export; SQLite persistence;
  Markdown/JSON/PDF exporters (student & teacher variants).
- `LessonForge.Tests` — xUnit: chunker, vector index, Wikipedia client (fixtures), DTO JSON, export.
- `web/` — React + TypeScript (Vite): new-lesson form, live run view with review gates, lesson view
  (plan/quiz tabs, student/teacher toggle, export), history.

## Run LessonForge from a clean checkout

### Windows (recommended)

Install these prerequisites once:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not just the runtime)
- [Node.js 22.12+ or 24+](https://nodejs.org/)
- [Ollama for Windows](https://ollama.com/download/windows); the startup script launches its service if needed
- Git

After installing the prerequisites, clone the repository and start it with
**one PowerShell command from the repository root**, with no old API/Vite servers running:

```powershell
git clone https://github.com/dvdhtzr/LessonForge.git
cd LessonForge
.\scripts\start.ps1
```

On each run, `start.ps1` checks the .NET SDK and Node.js versions, starts the Ollama
server if it is not already running, checks the committed `ChatModel` and
`EmbeddingModel`, downloads any missing Ollama models (several gigabytes on the
first run), and installs frontend dependencies if missing or changed. It launches
the API and Vite in separate terminals, waits until both are available, then
opens **http://localhost:5173** automatically. Model downloads may require internet.

If you prefer to run full local verification before starting, use
`.\scripts\setup.ps1` separately.
That script checks prerequisites, pulls models and runs build, xUnit, lint and
frontend production-build checks. The startup script avoids running the entire
verification gate on every launch.

Keep the API and Vite terminals open while using LessonForge; the Ollama terminal
will appear only if the Ollama service wasn't already running. Close or Ctrl+C
in the terminals to stop each server. The launcher refuses to start if ports
5173 or 5174 are already in use, rather than silently starting duplicate servers.

**No `$env:` settings, secrets, API keys, or external model subscriptions are required.**

### Starting services manually (Windows, macOS, or Linux)

After installing the prerequisites, make sure these exact models are available:

```bash
ollama pull gemma4:latest
ollama pull nomic-embed-text:v1.5
```

For non-Windows installations, ensure the Ollama server is running with
`ollama serve` if it is not already started. From the repository root,
start the API in one terminal:

```bash
dotnet run --project LessonForge.Api --launch-profile http
```

Start the frontend in a second terminal:

```bash
cd web
npm ci
npm run dev
```

The API uses **http://localhost:5174**, and Vite uses **http://localhost:5173**.
The Vite proxy points to the committed API address; it does not need an
environment variable.

### Model and application settings

All runtime model choices live in **`LessonForge.Api/appsettings.json`**.
The API reads its Ollama settings from that file rather than inheriting model
overrides from an old shell session. It logs the selected chat model, embedding
model, endpoint, and output budget on startup, making it easy to confirm the
expected configuration is running.

To intentionally change models, edit the tracked config file and ensure the
matching Ollama model has been installed. Re-run `scripts/setup.ps1` and
restart the API after changing it. Models remain local to your machine; the
repository contains the install commands and configuration, not the model weights.

The live research step still needs internet access to Wikipedia and external
sources, but local LLM inference does not require a paid API.

### Reliable structured outputs

All four agents explicitly request **JSON schema** output from Ollama,
and disable separate reasoning output for structured tasks. Rather than accessing `AgentResponse<T>.Result` directly, LessonForge
reads the final response, deserializes the expected DTO, and rejects missing,
empty or otherwise incomplete results with stage-specific diagnostics.

The Assessment Writer validates every question's stem, options, correct-answer
index, explanation, and supporting citations, and enforces the educator's
requested numbers of questions and answer choices.

The Accuracy Editor now returns only a **compact verification review**:
`flags`, `citation_coverage` and `notes`. The application assembles the
final result using the educator-approved plan and generated quiz, unchanged.
This prevents a local model from exhaustively repeating (or accidentally
changing) the entire lesson and answer key just to report its findings.
Source excerpts are capped for the final review call; insufficient evidence
must be called out rather than treated as a successful independent fact check.
Coverage is **AI-estimated**, not a guarantee of factual accuracy. These
checks enforce structure, not factual correctness; educators should review the
finished materials and flagged issues.

This avoids depending on how a model exposes its internal reasoning or final
answer. The committed Gemma 4 configuration has already reached the research
approval gate in a local smoke test.
No generated output is silently replaced by a fabricated lesson or quiz.

### Optional Perspective Analyst

On the **Create a new lesson** page, enable **Explore differing perspectives** to run a
specialized fifth agent. This is useful for historical interpretation, ethical and
philosophical disagreement, comparative religious history, and other subjects where
credible evidence supports more than one meaningful interpretation. It is *not*
a request to manufacture a controversy.

The agent returns a typed `PerspectiveAnalysisOutput` including:

- `status`: `supported_disagreement`, `no_substantive_disagreement` or `insufficient_evidence`
- A small list of sourced perspectives, each with its evidence and limitations (only for
  supported disagreement)
- Areas of agreement, evidence limitations, and age-appropriate discussion questions

The original Research Librarian continues producing the same compact
`ResearchDraftOutput` shape; adding this optional feature does not make the factual
agent generate or invent alternative viewpoints. The workflow appends the vetted
analysis to its research envelope after the dedicated agent runs.

The UI presents the analysis and real source links during the existing **research
approval** gate, and it remains visible in completed lessons and Markdown, JSON and
PDF exports. When off, LessonForge follows the original four-agent MAF graph.

**Teaching and source limitations:** This is an evidence-gathering aid, not a neutral
arbiter that can definitively establish scholarly consensus. Wikipedia and reachable
references do not guarantee representative coverage of academic scholarship or primary
sources. A limited source collection may require a finding of insufficient evidence.
The educator should inspect the cited sources, decide which perspectives deserve
classroom attention, and retain final responsibility for framing and age suitability.

**Manual acceptance:** Test the French Revolution (age 12) with the option on;
review and approve credible sourced interpretations and check that the finished
lesson and quiz use them. Repeat with photosynthesis and confirm the analyst is
allowed to report **no substantive disagreement** rather than fabricate opposing
scientific claims. Run each topic once with the toggle off to confirm the baseline
path is unchanged.

### Local inference timeout, output budget and research progress

The four local MAF agents now explicitly request up to **8192 output tokens** per
structured response, instead of relying on the model's default generation cap.
Configure `LessonForge:Llm:MaxOutputTokens` in `LessonForge.Api/appsettings.json`. If Ollama reports a
`length` finish reason, the agent rejects the truncated output with a clear token-budget
error; it never tries to parse or silently repair an incomplete lesson.

The Research Librarian requests a compact JSON shape with `grounded_facts[].citations`
(a plural array of `{title, url}` objects) and no model-authored source inventory.
The retrieval system supplies the authoritative source inventory. The research
prompt also caps excerpts to stay usable in small local context windows.

**Output budget is not context length:** the output-token cap does not increase how
much source material Ollama can hold. Ollama may allocate only **4K context** on
systems with less than 24 GiB VRAM; check `ollama ps` for the `CONTEXT` actually
allocated. If a model's context is insufficient, use Ollama's context settings or a
larger-context model configuration, noting that a larger context uses more memory.
See the Ollama context-length documentation.

The OpenAI-compatible client would otherwise time out after roughly 100 seconds.
LessonForge configures `NetworkTimeoutSeconds` (600 by default), `MaxRetries`
(0 by default) and `MaxOutputTokens` (8192 by default) in the same committed
`appsettings.json`. Adjust that file directly if needed; restart the API
to apply changes. No environment-variable overrides are part of setup.

The Research Librarian emits progress for source retrieval, embedding, evidence search,
and local report generation. Progress appears on the run page and in API logs.
A failed stage is reported with its failure details rather than a generic empty result.
A longer timeout is not a guarantee of successful inference: check Ollama availability
and model memory usage if generation still fails.

## Validating locally (required before merging)

LessonForge uses **local verification** while GitHub Actions is unavailable. On a development
machine with the .NET 10 SDK, Node.js and npm, run the complete gate before merging a PR.
The unit tests use fixtures/fakes; they do not need a running Ollama server or live Wikipedia.
Dependency restore/install may still require internet access.

**Windows / PowerShell** (run from the repository root; setup already runs this):

```powershell
.\scripts\check.ps1
```

**macOS / Linux**:

```bash
./scripts/check.sh
```

| Script | Validation |
|--------|------------|
| `scripts/build.ps1` | Build API and xUnit projects in Release mode |
| `scripts/test.ps1` | Run all xUnit tests |
| `scripts/web.ps1` | Reuse unchanged dependencies; otherwise `npm ci`; lint, type-check, build |
| `scripts/check.ps1` | Run the complete local gate; fail on any nonzero .NET/npm exit |
| `scripts/ollama-prerequisites.ps1` | Validate that Ollama and the configured models are available |
| `scripts/setup.ps1` | Initial Windows setup: check tools, pull missing models, run verification |
| `scripts/start.ps1` | Single command: check prerequisites, start Ollama, pull missing models, prepare web dependencies, start API/frontend, open browser |
| `scripts/run-ollama.ps1` | Start Ollama service in a dedicated terminal when absent |

The `*.sh` equivalents run the same checks. A successful check prints
`==> All local checks passed.` and must not do so if a command fails.

**Windows note:** `npm ci` removes/recreates `node_modules`, which can fail with
`EPERM` when the Vite dev server has a native Rolldown binary loaded. Stop the Vite server
before the **first** run (or after a lockfile/Node version change), then rerun the checks.
After a successful installation, the verification script records the lockfile hash and Node
version inside `node_modules` and skips repeat installs until one changes.
This makes normal edit/test iterations safe while the dev server is running.

**Live application smoke test (separate from the automated gate):** Start Ollama, the API,
and the Vite frontend; generate a lesson, watch the research and stage events, respond to
both review gates, confirm the completed lesson is saved, and try a student and teacher
export. This requires Ollama and access to Wikipedia/references.

At the research review gate, verify that **all** grounded facts are readable, each shows
a supporting citation link, and the full source inventory can be expanded. Approval is
blocked if the received facts have missing statements or invalid citations; use **Edit**
to inspect the original JSON. The API logs fact/statement/citation counts without logging
private research content. A failed approval POST leaves the review dialog open rather than
silently losing the decision.

## Hosted CI

GitHub Actions CI is temporarily disabled because of an account billing issue. Local
verification is the merge gate until hosted CI is restored. Record local check results
in the pull request. If GitHub branch protection or a ruleset requires a legacy CI
status check, remove that requirement in GitHub repository settings separately.

## Endpoints

| Method | Route | Purpose |
|--------|-------|---------|
| POST | `/api/lessons/generate?interactive=true` | Start a run; streams SSE |
| POST | `/api/lessons/gate/{runId}` | Approve/edit/abort a review gate |
| GET | `/api/lessons` | List saved lessons |
| GET | `/api/lessons/{id}` | Fetch a saved lesson |
| GET | `/api/lessons/{id}/export?format=md\|json\|pdf&audience=student\|teacher` | Download |

## License

Licensed under the [Apache License 2.0](LICENSE) — see `LICENSE` and `NOTICE`.
