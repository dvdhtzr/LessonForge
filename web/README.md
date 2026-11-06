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

The web app talks to the LessonForge API. Start the API first (defaults to `http://localhost:5174`),
then:

```bash
cd web
npm install
npm run dev
```

Open http://localhost:5173.

If your API runs on a different address, point the dev server at it:

```bash
LESSONFORGE_API=http://localhost:5000 npm run dev
```

`/api/*` requests are proxied to that address (see `vite.config.ts`), so there are no CORS
surprises in development.

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
