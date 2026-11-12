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
- `logs/`: API output per model (without question/evidence trace payloads).
- `diagnostics/<run-id>.answer-uniqueness.jsonl`: **opt-in, local-only** per-run, append-and-flush records. Each captures the original/replacement question, each model verdict, bounded source excerpt with reference URLs, accepted/rejected status, or a coded failure. These records survive a failed Answer Verifier run.
- `answer-uniqueness-diagnostics.json`: consolidated copy, indexed by model, topic case and run ID for easier human review. The main `report.json` has only its relative trace path and record count.

The benchmark alone enables trace collection by setting `LESSONFORGE_EVAL_DIAGNOSTICS_DIR` on its isolated API child process. Normal LessonForge runs do **not** create these files unless that environment variable is set explicitly. Trace data is capped at 256 events and 1.5 MB per run, with bounded question text, individual choices, reviewer reasons and source evidence. The writer flushes after every event, so partial traces survive rejected model responses and most interrupted runs; no raw provider errors or raw full model responses are recorded. Files contain lesson text and potentially private user input: **keep them local, inspect before uploading, and never commit them to Git**. Trace records are diagnostic, not proof of a uniquely correct answer.

A STRUCTURAL_PASS is **not** a factual-accuracy pass. Human review must check the answers, grade calibration, citation entailment, teaching usefulness, review false negatives, and actual source text. Source retrieval varies between runs; separate model failures from network/source failures. The harness exercises `interactive=false`, so educators' approval gates need an additional acceptance test.

## Proposed GREEN requirements before choosing a release model

- Existing `scripts/check.ps1` passes and no rights or correctness safeguards are relaxed.
- Three full repetitions per candidate (nine case runs) succeed structurally, without truncated/incomplete reviews or timeout failures.
- A manual factual and pedagogical audit of all nine results finds no incorrect marked answers or unsupported critical claims.
- Measure cold and warm timing, RAM, and Ollama GPU/CPU offload on a **representative Windows 11, 16 GB, CPU-only** computer. Benchmarks from a GPU workstation do not establish that experience.
- Compare against a known-working larger model when feasible; separately validate manual review gates and optional perspectives.

If no small model qualifies, consider a bounded lightweight teaching profile or higher minimum specification as an explicit, separate decision. Never quietly disable validation to improve success rates.
