import type { LessonPlanOutput } from '../types'

export default function PlanRenderer({ plan }: { plan: LessonPlanOutput }) {
  return (
    <div>
      <h2 style={{ marginBottom: 2 }}>{plan.title}</h2>
      <p className="muted">{plan.grade_band}</p>
      {plan.overview && <p>{plan.overview}</p>}

      {plan.learning_objectives?.length > 0 && (
        <>
          <h3>Learning objectives</h3>
          <ul>{plan.learning_objectives.map((o, i) => <li key={i} className="objective">{o}</li>)}</ul>
        </>
      )}

      {plan.materials?.length > 0 && (
        <>
          <h3>Materials</h3>
          <ul>{plan.materials.map((m, i) => <li key={i}>{m}</li>)}</ul>
        </>
      )}

      {plan.sections?.map((s, i) => (
        <section key={i}>
          <h3>{s.title}{s.duration_minutes > 0 && <span className="muted"> · {s.duration_minutes} min</span>}</h3>
          <p style={{ whiteSpace: 'pre-wrap' }}>{s.body}</p>
          {s.activities?.length > 0 && (
            <>
              <strong>Activities</strong>
              <ul>{s.activities.map((a, j) => <li key={j}>{a}</li>)}</ul>
            </>
          )}
        </section>
      ))}

      {plan.vocabulary?.length > 0 && (
        <>
          <h3>Vocabulary</h3>
          <ul>{plan.vocabulary.map((v, i) => <li key={i}><strong>{v.term}</strong> — {v.definition}</li>)}</ul>
        </>
      )}

      {plan.citations?.length > 0 && (
        <>
          <h3>Sources</h3>
          <ul>{plan.citations.map((c, i) => (
            <li key={i}><a href={c.url} target="_blank" rel="noreferrer">{c.title || c.url}</a></li>
          ))}</ul>
        </>
      )}
    </div>
  )
}
