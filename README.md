# LessonForge

A local-first app that turns a **topic + target audience age** into a **structured lesson plan and
a citation-backed multiple-choice quiz**, grounded in facts retrieved from **Wikipedia and its
cited sources** via RAG.

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
| Research | Research Librarian | Searches Wikipedia (seed + closely-linked articles) + fetches freely-available cited references, chunks + embeds them into a per-run in-memory vector index, returns grounded facts with citations |
| *gate* | — | Educator reviews/edits/approves the research |
| Lesson Planner | Curriculum Designer | Builds an age-calibrated plan: objectives, sections, activities, vocabulary |
| *gate* | — | Educator reviews/edits/approves the plan |
| Quiz Generator | Assessment Writer | Writes MCQs (defaults: 10 questions × 4 options, difficulty auto-scaled to age) with citation-backed explanations |
| Fact-Check | Accuracy Editor | Verifies claims/answers against sources, repairs citations, flags unsupported statements |

### Key design decisions

- **RAG index is in-memory and per-request** — built fresh from live Wikipedia each run, then
  discarded. Only the finished lesson/quiz is persisted (SQLite). The index is a small hand-rolled
  cosine-similarity store over the standard `IEmbeddingGenerator` abstraction, so it needs no vector
  DB and is trivially testable.
- **Models (local Ollama, configured in Git):** `gemma4:latest` for all four agents and
  `nomic-embed-text:v1.5` for the RAG embeddings. Both names are defined in the
  committed `LessonForge.Api/appsettings.json`. No API keys, cloud services, or
  manually configured environment variables are necessary.
- **Streaming + gates:** the API streams Server-Sent Events (`run` → `stage*` → `gate*` →
  `complete` → `done`). Gates map to MAF `RequestPort`s; the UI approves/edits/aborts. Active runs
  live in memory (single-user v1).

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

All four MAF agents ask Ollama for **JSON mode** and request no separate reasoning
output on their schema-shaped tasks. Instead of accessing `AgentResponse<T>.Result`
directly, LessonForge reads the final response text, deserializes the required DTO,
and rejects empty, invalid or obviously incomplete results with stage-specific
diagnostics. These checks do not assert that the educational facts are correct;
the Accuracy Editor and educator review remain necessary.

This avoids depending on how a model exposes its internal reasoning or final
answer. The committed Gemma 4 configuration has already reached the research
approval gate in a local smoke test.
No generated output is silently replaced by a fabricated lesson or quiz.

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
