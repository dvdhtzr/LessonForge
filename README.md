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
   web/                            LessonForge.Api         LessonForge.Core          qwen3.8:27b
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
- **Models (local Ollama, configurable):** `qwen3.8:27b` (primary), `qwen3.5:9b` (optional
  faster model), `nomic-embed-text:v1.5` (768-dim embeddings). Set under
  `LessonForge:Llm` in `LessonForge.Api/appsettings.json` or via `LessonForge__Llm__*`
  environment variables. The current workflow uses the **primary model** for all four
  agents; the fast-model setting is available but is not yet assigned to a workflow stage.
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

## Running it

**Prerequisites:** .NET 10 SDK, Node.js 20+, and [Ollama](https://ollama.com) with the models:

```bash
ollama pull qwen3.8:27b
ollama pull qwen3.5:9b
ollama pull nomic-embed-text:v1.5
ollama serve
```

**API** (listens on http://localhost:5174):

```bash
dotnet run --project LessonForge.Api --launch-profile http
```

**Frontend** (http://localhost:5173, proxies `/api` to the API):

```bash
cd web
npm install
npm run dev
```

Open http://localhost:5173, enter a topic and age, and generate.

### Local inference timeout and research progress

The OpenAI-compatible .NET client defaults to a 100-second network timeout. A local
27-billion-parameter model may take longer to respond, especially when starting cold or
running partially from CPU/system memory. LessonForge configures
`LessonForge:Llm:NetworkTimeoutSeconds` (default **600**) and
`LessonForge:Llm:MaxRetries` (default **0**) so a slow response isn't automatically
generated repeatedly. You can override them without changing source:

```powershell
$env:LessonForge__Llm__NetworkTimeoutSeconds = '900'
$env:LessonForge__Llm__MaxRetries = '0'
# Optional faster smoke test; does not change committed application defaults:
$env:LessonForge__Llm__ChatModel = 'qwen3.5:9b'
```

Set variables in the API's PowerShell session **before** starting `dotnet run`.
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

**Windows / PowerShell** (run from the repository root):

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
