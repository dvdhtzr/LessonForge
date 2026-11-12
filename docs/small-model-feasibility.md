# Milestone A — Small-model feasibility

Status: evaluation only, not a Windows installer or a qualified model. This experiment preserves the existing LessonForge workflow, source permission rules, citation validation, JSON schemas, and accuracy-review logic.

## Goal

Find the smallest local Ollama model that can generate a complete, educationally useful, cited lesson and quiz. Start with qwen3:4b, then compare qwen3.5:2b and qwen3:1.7b. Prefer complete and reliable results over minimum download size.

## Windows commands (Node 22+, .NET 10 SDK, Ollama)

1. From the repository root: `ollama pull nomic-embed-text:v1.5`
2. Pull first candidate: `ollama pull qwen3:4b`
3. Test harness: `node --test .\scripts\benchmark-small-models.test.mjs`
4. Smoke run: `node .\scripts\benchmark-small-models.mjs --models qwen3:4b --suite smoke`
5. If viable, pull `qwen3.5:2b` and `qwen3:1.7b`, then run: `node .\scripts\benchmark-small-models.mjs --models qwen3:4b,qwen3.5:2b,qwen3:1.7b --suite full`

The harness publishes the API to its own temporary location, changes only the published copy's chat-model configuration, runs the API on localhost:5189, and writes separate SQLite databases. It never edits repository configuration, installs models, or stops the user's Ollama service. Change `--port` if necessary. Output is an OS temporary directory unless `--output` is supplied. Do not commit reports: they contain the complete generated lessons.

`smoke` is Photosynthesis for age 10 with the normal ten four-option questions. `full` also tests causes of World War I (age 15) and Carmen Carrozza (age 14) to exercise history and named-subject safeguards. These are illustrative cases, not a representative statistical sample.

## Reports and interpretation

- `summary.md`: status, elapsed time, source count, findings and AI-estimated coverage.
- `report.json`: model digests, machine info, stage events, persisted lesson and structural diagnostics.
- `logs/`: API output per model.

A STRUCTURAL_PASS is **not** a factual-accuracy pass. Human review must check the answers, grade calibration, citation entailment, teaching usefulness, review false negatives, and actual source text. Source retrieval varies between runs; separate model failures from network/source failures. The harness exercises `interactive=false`, so educators' approval gates need an additional acceptance test.

## Proposed GREEN requirements before choosing a release model

- Existing `scripts/check.ps1` passes and no rights or correctness safeguards are relaxed.
- Three full repetitions per candidate (nine case runs) succeed structurally, without truncated/incomplete reviews or timeout failures.
- A manual factual and pedagogical audit of all nine results finds no incorrect marked answers or unsupported critical claims.
- Measure cold and warm timing, RAM, and Ollama GPU/CPU offload on a **representative Windows 11, 16 GB, CPU-only** computer. Benchmarks from a GPU workstation do not establish that experience.
- Compare against a known-working larger model when feasible; separately validate manual review gates and optional perspectives.

If no small model qualifies, consider a bounded lightweight teaching profile or higher minimum specification as an explicit, separate decision. Never quietly disable validation to improve success rates.
