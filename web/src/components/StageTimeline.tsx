import type { StageStatus } from '../types'

export const STAGES = [
  { id: 'Research', role: 'Research Librarian', label: 'Gathering and citing sources', mark: 'R' },
  { id: 'PerspectiveAnalysis', role: 'Perspective Analyst', label: 'Comparing evidence and interpretations', mark: 'P' },
  { id: 'LessonPlanner', role: 'Curriculum Designer', label: 'Designing the lesson', mark: 'C' },
  { id: 'QuizGenerator', role: 'Assessment Writer', label: 'Writing the quiz', mark: 'A' },
  { id: 'FactCheck', role: 'Accuracy Editor', label: 'Checking claims and citations', mark: 'E' },
] as const

export function getStage(id: string | null | undefined) {
  return STAGES.find((stage) => stage.id === id)
}

export default function StageTimeline({ statuses, explorePerspectives = false }: {
  statuses: Record<string, StageStatus>
  explorePerspectives?: boolean
}) {
  return (
    <ol className="timeline" aria-label="Lesson creation stages">
      {STAGES.filter((s) => s.id !== 'PerspectiveAnalysis' || explorePerspectives).map((stage) => {
        const status = statuses[stage.id]
        return (
          <li key={stage.id} className={`timeline-row ${status ?? ''}`}
            aria-current={status === 'started' ? 'step' : undefined}>
            <span className={`stage-symbol ${status ?? ''}`} aria-hidden="true">{stage.mark}</span>
            <span className="stage-description">
              <strong>{stage.role}</strong>
              <span>{stage.label}</span>
            </span>
            {status === 'completed' && <span className="pill good">Done</span>}
            {status === 'started' && <span className="pill warn">Working</span>}
            {status === 'failed' && <span className="pill bad">Failed</span>}
          </li>
        )
      })}
    </ol>
  )
}
