import type { StageStatus } from '../types'
import { STAGES } from './stageDefinitions'

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
