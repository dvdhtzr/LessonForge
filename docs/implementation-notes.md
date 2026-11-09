# Implementation notes

Design and correctness decisions behind LessonForge, in more depth than the
[README](../README.md). These notes explain *why* the system is built the way it
is — the failure modes it guards against and the rights boundaries it enforces.

For source architecture and the rights policy specifically, see
[`docs/source-registry.md`](source-registry.md).

---

## Key design decisions

- **RAG index is in-memory and per-request** — built fresh from live sources each run,
  then discarded. Only the finished lesson/quiz is persisted (SQLite). The index is a
  small hand-rolled cosine-similarity store over the standard `IEmbeddingGenerator`
  abstraction, so it needs no vector DB and is trivially testable.
- **Models (local Ollama, configured in Git):** `gemma4:latest` for all four agents and
  `nomic-embed-text:v1.5` for the RAG embeddings. Both names are defined in the committed
  `LessonForge.Api/appsettings.json`. No API keys, cloud services, or manually configured
  environment variables are necessary.
- **Bounded Accuracy Editor review:** instead of requesting an unbounded audit of every
  section and quiz question in one model response, the final MAF executor validates small
  batches of two items, using source excerpts relevant to each scope. A `finish_reason=length`
  response is never accepted; two-item batches are retried as separate items, and a failed
  single-item review fails the entire stage without persisting a partial audit. Valid reports
  are aggregated, with AI-estimated citation coverage weighted by reviewed items. This uses
  additional local model calls to avoid the fragile single-response token budget.
- **Structured Accuracy Review:** the final agent produces advisory findings with
  High / Medium / Low / Informational severity, a controlled issue category, a lesson-section
  or quiz-question reference (S# / Q#), a concrete educator recommendation and an optional
  AI-written evidence summary. Severity uses a documented instructional rubric. C# rejects
  malformed categories, absent actions and out-of-range locations; it supplies repeatable
  finding IDs and only consolidates obvious duplicates at the same location/category/focal
  claim. The review UI groups findings and links them to relevant lesson content. Historical
  SQLite lessons with the earlier three-field flags remain readable. Teacher exports carry
  review actions; student exports do not expose flags. This does **not** independently prove
  claims true or guarantee that the review catches every omission, especially where the
  source/lesson excerpts were bounded.
- **Bounded Perspective Analyst recovery:** a supported disagreement must have two complete
  viewpoints with substantive evidence, limitations and retrieved citation URLs. If a local
  model returns a structurally plausible but unverifiable second viewpoint, the analysis is
  regenerated once with a smaller, stricter prompt. A second failed validation results in an
  **explicitly inconclusive** analysis with no accepted perspectives and a clear explanation
  for educator review. No missing fields, fabricated citations or unsupported opposing
  positions are silently repaired; Ollama transport, generation and parse failures still
  surface as errors.
- **Streaming + gates:** the API streams Server-Sent Events (`run` → `stage*` → `gate*` →
  `complete` → `done`). Gates map to MAF `RequestPort`s; the UI approves/edits/aborts. Active
  runs live in memory (single-user v1).

---

## Reliable structured outputs

All four agents explicitly request **JSON schema** output from Ollama, and disable separate
reasoning output for structured tasks. Rather than accessing `AgentResponse<T>.Result` directly,
LessonForge reads the final response, deserializes the expected DTO, and rejects missing, empty
or otherwise incomplete results with stage-specific diagnostics.

The Assessment Writer validates every question's stem, options, correct-answer index,
explanation, and supporting citations, and enforces the educator's requested numbers of
questions and answer choices.

The Accuracy Editor returns only a **compact verification review**: `flags`, `citation_coverage`
and `notes`. The application assembles the final result using the educator-approved plan and
generated quiz, unchanged. This prevents a local model from exhaustively repeating (or
accidentally changing) the entire lesson and answer key just to report its findings. Source
excerpts are capped for the final review call; insufficient evidence must be called out rather
than treated as a successful independent fact check. Coverage is **AI-estimated**, not a
guarantee of factual accuracy. These checks enforce structure, not factual correctness.

This avoids depending on how a model exposes its internal reasoning or final answer. No
generated output is silently replaced by a fabricated lesson or quiz.

---

## Accuracy Editor — missing finding locations

The Accuracy Editor audits lesson sections and quiz questions in independent, small scopes.
Each educator-visible finding must identify the audited item (`S3`, `Q1`, or `GENERAL`). A
valid Ollama JSON response can occasionally omit the `location` field even when all other
finding fields are present.

When the server is auditing **exactly one** section/question, that host-selected identifier is
unambiguous; the structured-output boundary fills only a missing/blank location with that ID.
It never overwrites a supplied location, changes finding content, or relaxes
severity/category/recommendation validation. For a two-item scope with missing locations, the
batcher instead retries the individual scopes: no guesses about which finding belongs to which
item. A single-item result that remains otherwise malformed still fails closed, and truncated
responses remain rejected.

The Accuracy Editor also performs **one bounded full regeneration** of a single S#/Q# scope if
its structured response still omits finding locations after the host-based recovery. It retains
existing strict validation; repeated failure still stops rather than silently reporting
successful fact checking.

---

## Named-subject identity protection

An explicit named topic such as **Carmen Carrozza** must not be silently replaced with a fuzzy
Wikipedia search match such as **Jesús Carroza**.

LessonForge probes short exact Wikipedia article titles before ranked search results, including
when a user types the title in lowercase. For explicitly named subjects, it accepts only the
matching canonical title and does not automatically expand to other people's linked biographies.
A failed direct lookup may search for the **same** exact title, but never chooses a similar-name
Wikipedia result as a fallback. When Wikipedia cannot provide an exact match, other
rights-reviewed providers may contribute only records that explicitly name the full requested
subject. For two-part named topics, surname-only catalog matches are excluded even if Wikipedia
matches.

The Research Librarian also requires named-topic summaries to identify the requested subject. If
a valid JSON response describes a different subject, it gets **one evidence-grounded,
summary-only regeneration**, preserving the original citation-validated facts. If the replacement
still names the wrong subject, the run stops before educator approval or lesson planning. This is
a deterministic identity safeguard, not proof that every sentence or citation is factually
accurate; educator review remains essential.

---

## Research-summary completeness and automatic repair

The Research Librarian validates the `topic_summary` before any educator review or
lesson-planning handoff. JSON schema alone can accept an obviously unfinished string (for
example, a summary ending in "derived from the concept of"). A conservative deterministic check
rejects missing sentence-ending punctuation, terminal ellipses and dangling conjunctions or
prepositions.

If detected, LessonForge performs **one summary-only local Ollama regeneration** using the same
rights-eligible excerpts and already verified grounded facts. It preserves the existing 8–10
cited facts, source inventory and provenance. It does not complete a cut-off sentence by guessing
or rerun the whole multi-agent workflow. The progress stream reports that repair is underway.

If the new summary is still incomplete or model generation fails, the research stage fails with a
clear explanation rather than presenting the fragment for educator approval. The check is
heuristic: punctuation does not establish that a sentence is factually correct, grammatically
valid in every case, or supported by the cited passages.

---

## Duplicate citation handling

Each distinct source URL appears only once within a research fact, a lesson-plan Sources list, a
quiz answer's citations, a perspective's references, and the retrieved-source provenance list.
The host removes exact/equivalent URL repeats *after* citation validation, keeping the first
approved title and URL in order. Different source URLs remain independent even if their page
titles match. The same source may still legitimately support multiple **different** facts.
Previously saved lessons are normalized when loaded for display and export; their stored JSON is
not rewritten or migrated.

---

## Optional Perspective Analyst

On the **Create a new lesson** page, enabling **Explore differing perspectives** runs a
specialized fifth agent. This is useful for historical interpretation, ethical and philosophical
disagreement, comparative religious history, and other subjects where credible evidence supports
more than one meaningful interpretation. It is *not* a request to manufacture a controversy.

The agent returns a typed `PerspectiveAnalysisOutput` including:

- `status`: `supported_disagreement`, `no_substantive_disagreement` or `insufficient_evidence`
- A small list of sourced perspectives, each with its evidence and limitations (only for
  supported disagreement)
- Areas of agreement, evidence limitations, and age-appropriate discussion questions

The original Research Librarian continues producing the same compact research shape; adding this
optional feature does not make the factual agent generate or invent alternative viewpoints. The
workflow appends the vetted analysis to its research envelope after the dedicated agent runs. The
UI presents the analysis and real source links during the existing **research approval** gate,
and it remains visible in completed lessons and Markdown, JSON and PDF exports. When off,
LessonForge follows the original four-agent MAF graph.

**Teaching and source limitations:** this is an evidence-gathering aid, not a neutral arbiter
that can definitively establish scholarly consensus. Wikipedia and reachable references do not
guarantee representative coverage of academic scholarship or primary sources. A limited source
collection may require a finding of insufficient evidence. The educator should inspect the cited
sources, decide which perspectives deserve classroom attention, and retain final responsibility
for framing and age suitability.

### Evidence-based differing-perspectives suggestion

The new-lesson form makes one bounded, anonymous request to
`GET /api/lessons/perspectives-signal?topic=...` after the topic stops changing. The API reads
the **initial English Wikipedia article** through the already-reviewed MediaWiki API and checks
its text for a **cluster** of concrete methodological, scholarly, ethical or historical dispute
indicators. This is a heuristic recommendation, not a truth verdict. There is **no LLM call**,
source ingest, or charged provider in this preflight; it is independent of the MAF perspective
analysis.

When supported, the form **offers** an unchecked "Explore differing perspectives" option and
identifies the article inspected. Neutral articles such as gravity, photosynthesis and
boiling-point explanations should not recommend a debate based on incidental words such as
"theory". An educator can always opt in via **Advanced options**; failure to fetch the article
never prevents creating a lesson. Changing the topic clears the old recommendation, and prior
in-flight requests are cancelled.

---

## Local inference timeout, output budget and research progress

The four local MAF agents explicitly request up to **8192 output tokens** per structured
response, instead of relying on the model's default generation cap. Configure
`LessonForge:Llm:MaxOutputTokens` in `LessonForge.Api/appsettings.json`. If Ollama reports a
`length` finish reason, the agent rejects the truncated output with a clear token-budget error;
it never tries to parse or silently repair an incomplete lesson.

**Output budget is not context length:** the output-token cap does not increase how much source
material Ollama can hold. Ollama may allocate only **4K context** on systems with less than 24 GiB
VRAM; check `ollama ps` for the `CONTEXT` actually allocated. If a model's context is
insufficient, use Ollama's context settings or a larger-context model configuration, noting that
a larger context uses more memory.

The OpenAI-compatible client would otherwise time out after roughly 100 seconds. LessonForge
configures `NetworkTimeoutSeconds` (600 by default), `MaxRetries` (0 by default) and
`MaxOutputTokens` (8192 by default) in the same committed `appsettings.json`. Adjust that file
directly if needed; restart the API to apply changes.

The Research Librarian emits progress for source retrieval, embedding, evidence search, and local
report generation. Progress appears on the run page and in API logs. A failed stage is reported
with its failure details rather than a generic empty result.

---

## Reading layout and live agent activity

Research source links show small, locally rendered provider/article badges (Wikipedia, USGS,
Library of Congress, Project Gutenberg and Smithsonian). Teacher lesson pages also have a
**collapsed Research source library** showing retrieved source identity and licensing metadata.
No remote logos, favicons or article images are fetched, since those require independent image
licensing and source-URL controls.

The lesson-generation page highlights the actual active agent, shows live research/progress text,
counts **completed workflow stages** (not estimated time), and offers an expandable history of
the eight most recent real SSE events. Educator review is shown as a paused checkpoint. No
synthetic agent messages, fake progress percentages or fabricated progress animation are
introduced; a small active indicator respects reduced-motion preferences.
