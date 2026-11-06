import type { StageStatus } from '../types'
import { getStage, STAGES } from './StageTimeline'

export interface ActivityEntry {
  id: number
  kind: 'connection' | 'stage' | 'progress' | 'review'
  stage?: string
  message: string
}

interface Props {
  connected: boolean
  explorePerspectives: boolean
  latestStageId: string | null
  statuses: Record<string, StageStatus>
  progressMessage: string | null
  waitingForReview: boolean
  error: string | null
  aborted: string | null
  entries: ActivityEntry[]
}

/**
 * A prominent but factual display of MAF/SSE activity.
 * Nothing moves unless a real stage/progress event is received, and we do not
 * show made-up percentages, token counts or agent-to-agent conversations.
 */
export default function AgentActivity({
  connected, explorePerspectives, latestStageId, statuses, progressMessage,
  waitingForReview, error, aborted, entries,
}: Props) {
  const expectedStages = STAGES.filter((item) => item.id !== 'PerspectiveAnalysis' || explorePerspectives)
  const completedStages = expectedStages.filter((item) => statuses[item.id] === 'completed').length
  const stage = getStage(latestStageId)
  const isWorking = !waitingForReview && !error && !aborted &&
    !!stage && statuses[stage.id] === 'started'

  const title = error
    ? 'The crew encountered a problem'
    : aborted
      ? 'Lesson creation stopped'
      : waitingForReview
        ? 'Your review is ready'
        : isWorking
          ? `${stage?.role ?? 'Agent'} is working`
          : connected
            ? 'Preparing the next step'
            : 'Connecting to LessonForge'

  const detail = error
    ? 'Review the error below before starting another lesson.'
    : aborted
      ? 'No further agent activity is running.'
      : waitingForReview
        ? 'The workflow is paused until you approve, edit, or abort the review.'
        : progressMessage
          ? progressMessage
          : isWorking && stage
            ? stage.label
            : connected
              ? 'Connected. Waiting for the next workflow event.'
              : 'Establishing a connection to the local lesson service.'

  return (
    <section className={`agent-board ${isWorking ? 'agent-board--working' : ''}`}
      aria-label="Current agent activity">
      <div className="agent-board__headline">
        <span className={`agent-board__avatar ${isWorking ? 'agent-board__avatar--live' : ''}`}
          aria-hidden="true">{waitingForReview ? '✓' : stage?.mark ?? '✦'}</span>
        <div className="agent-board__copy" role="status" aria-live="polite" aria-atomic="true">
          <span className="agent-board__eyebrow">
            {waitingForReview ? 'Educator checkpoint' : isWorking ? 'Agent working now' : 'Lesson production'}
          </span>
          <h3>{title}</h3>
          <p>{detail}</p>
        </div>
        <span className={`agent-board__indicator ${isWorking ? 'agent-board__indicator--live' : ''}`}
          aria-hidden="true" />
      </div>

      <p className="agent-board__stage-count">
        {completedStages} of {expectedStages.length} workflow stages completed
      </p>

      {entries.length > 0 && (
        <details className="agent-board__history">
          <summary>Recent activity <span className="muted">({entries.length} events shown)</span></summary>
          <ol>
            {[...entries].reverse().map((entry) => (
              <li key={entry.id}>
                <span className="agent-board__event-type" aria-hidden="true">
                  {entry.kind === 'review' ? '✓' : entry.kind === 'progress' ? '·' : '•'}
                </span>
                <span>
                  {entry.stage && <strong>{getStage(entry.stage)?.role ?? entry.stage}: </strong>}
                  {entry.message}
                </span>
              </li>
            ))}
          </ol>
        </details>
      )}
    </section>
  )
}
