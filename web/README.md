# LessonForge Web

The front end for the LessonForge API — a friendly, educator-themed interface for turning any
topic into a citation-backed lesson plan and quiz. Built with **React 19 + TypeScript + Vite**.

## What you can do

- **Create a lesson** — enter a topic and your students' age; LessonForge researches, writes a
  lesson plan, and builds a matching quiz.
- **Watch it come together** — a live progress timeline shows each assistant (researcher, planner,
  quiz writer, fact-checker) as it works.
- **Review as you go** — optionally approve or edit the research and the plan before the quiz is
  written.
- **Switch between teacher and student views** — teacher view shows answers, explanations and
  sources; student view hides them.
- **Check the facts** — every lesson shows its citation coverage and flags any unsupported claims.
- **Export** — download any lesson as Markdown, JSON or PDF, in a teacher or student variant.
- **Find past lessons** under **My lessons**.

## Running it locally

Follow the [repository setup guide](../README.md) to install the .NET 10 SDK, Node.js,
Ollama and the configured models. On Windows, run `scripts/setup.ps1` and
`scripts/start.ps1` from the repository root; the scripts launch the API and frontend
without requiring environment variables.

To run just the frontend manually, start the API on `http://localhost:5174`,
then run:

```bash
cd web
npm ci
npm run dev
```

Open http://localhost:5173. The `/api/*` proxy is fixed to the API's documented
local address in `vite.config.ts`, so it works from a clean checkout.

## Scripts

| Command | What it does |
| --- | --- |
| `npm run dev` | Start the dev server with hot reload |
| `npm run build` | Type-check and build a production bundle into `dist/` |
| `npm run preview` | Preview the production build locally |
| `npm run lint` | Lint with [Oxlint](https://oxc.rs) |

## Project layout

```
src/
  api/client.ts      API calls + Server-Sent-Events parser for live generation
  pages/             NewLesson, RunView, LessonView, History
  components/        StageTimeline, GateReview, PlanRenderer, QuizRenderer
  types.ts           TypeScript mirrors of the API's DTOs
  index.css          The educator theme (warm paper, chalkboard-green accents)
```
