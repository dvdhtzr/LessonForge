import type { StageStatus } from '../types'

const STAGES: { id: string; label: string }[] = [
  { id: 'Research', label: 'Research Librarian — gathering & citing sources' },
  { id: 'PerspectiveAnalysis', label: 'Perspective Analyst — examining evidence and interpretations' },
  { id: 'LessonPlanner', label: 'Curriculum Designer — building the lesson' },
  { id: 'QuizGenerator', label: 'Assessment Writer — writing the quiz' },
  { id: 'FactCheck', label: 'Accuracy Editor — verifying citations' },
]

export default function StageTimeline({ statuses, explorePerspectives = false }: {
  statuses: Record<string, StageStatus>
  explorePerspectives?: boolean
}) {
  return (
    <ul className="timeline">
      {STAGES.filter((s) => s.id !== 'PerspectiveAnalysis' || explorePerspectives).map((s) => {
        const status = statuses[s.id]
        return (
          <li key={s.id}>
            <span className={`dot ${status ?? ''}`} />
            <span style={{ color: status ? 'var(--text)' : 'var(--muted)' }}>{s.label}</span>
            {status === 'completed' && <span className="pill good" style={{ marginLeft: 'auto' }}>done</span>}
            {status === 'started' && <span className="pill warn" style={{ marginLeft: 'auto' }}>working…</span>}
            {status === 'failed' && <span className="pill bad" style={{ marginLeft: 'auto' }}>failed</span>}
          </li>
        )
      })}
    </ul>
  )
}
