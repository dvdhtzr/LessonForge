# LessonForge

A local-first app that turns a **topic + target audience age** into a **structured lesson plan and
a citation-backed multiple-choice quiz**, grounded in facts retrieved from **Wikipedia and its
cited sources** via RAG.

It reuses the patterns from the [Guild](https://github.com/dvdhtzr/Guild) project — a .NET 10 /
**Microsoft Agent Framework (MAF)** multi-agent crew running against a local **Ollama** server — and
adds a web API, a React frontend, and a retrieval pipeline.

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
- **Models (local Ollama, configurable):** `qwen3.8:27b` (reasoning), `qwen3.5:9b` (fast sub-tasks),
  `nomic-embed-text:v1.5` (768-dim embeddings). Set under `LessonForge:Llm` in
  `LessonForge.Api/appsettings.json` or via `LessonForge__Llm__*` env vars.
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
dotnet run --project LessonForge/LessonForge.Api --launch-profile http
```

**Frontend** (http://localhost:5173, proxies `/api` to the API):

```bash
cd LessonForge/web
npm install
npm run dev
```

Open http://localhost:5173, enter a topic and age, and generate.

## Tests

```bash
dotnet test LessonForge/LessonForge.Tests
cd LessonForge/web && npm run build   # type-checks the frontend
```

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
