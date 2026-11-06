import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { api, generate } from '../api/client'
import type { GateEvent, LessonRequest, StageStatus } from '../types'
import StageTimeline from '../components/StageTimeline'
import AgentActivity, { type ActivityEntry } from '../components/AgentActivity'
import GateReview from '../components/GateReview'

interface RunState { request: LessonRequest; interactive: boolean }

export default function RunView() {
  const location = useLocation()
  const navigate = useNavigate()
  const state = location.state as RunState | null

  const [statuses, setStatuses] = useState<Record<string, StageStatus>>({})
  const [gate, setGate] = useState<GateEvent | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [aborted, setAborted] = useState<string | null>(null)
  const [runConnected, setRunConnected] = useState(false)
  const [progressMessage, setProgressMessage] = useState<string | null>(null)
  const [latestStageId, setLatestStageId] = useState<string | null>(null)
  const [activity, setActivity] = useState<ActivityEntry[]>([])
  const eventSeq = useRef(0)
  const runIdRef = useRef<string | null>(null)

  // Keep only actual events delivered over SSE. Never synthesize a progress
  // percentage or pretend agents are exchanging unseen messages.
  const addActivity = useCallback((entry: Omit<ActivityEntry, 'id'>) => {
    const id = ++eventSeq.current
    setActivity((previous) => {
      const last = previous[previous.length - 1]
      if (last && last.kind === entry.kind && last.stage === entry.stage && last.message === entry.message) {
        return previous
      }
      return [...previous, { ...entry, id }].slice(-8)
    })
  }, [])

  useEffect(() => {
    if (!state?.request) return
    const controller = new AbortController()

    // StrictMode replays effects in development. Defer the POST until its first
    // cleanup has had a chance to abort, so we start exactly one workflow.
    queueMicrotask(() => {
      if (controller.signal.aborted) return
      void generate(state.request, state.interactive, {
        onRun: (e) => {
          runIdRef.current = e.runId
          setRunConnected(true)
          addActivity({ kind: 'connection', message: 'Connected to the local lesson service.' })
        },
        onStage: (e) => {
          setStatuses((prev) => ({ ...prev, [e.name]: e.status }))
          setLatestStageId(e.name)
          setProgressMessage(null)
          const verb = e.status === 'started' ? 'Started' : e.status === 'completed' ? 'Completed' : 'Failed'
          addActivity({ kind: 'stage', stage: e.name, message: `${verb}.` })
        },
        onProgress: (e) => {
          setProgressMessage(e.message)
          addActivity({ kind: 'progress', stage: e.stage, message: e.message })
        },
        onGate: (e) => {
          setGate(e)
          setProgressMessage(null)
          addActivity({
            kind: 'review',
            message: e.stage === 'research' ? 'Research ready for educator review.' : 'Lesson plan ready for educator review.',
          })
        },
        onComplete: (e) => navigate(`/lessons/${e.id}`, { state: { lessonId: e.id } }),
        onError: (m) => setError(m),
        onAborted: (m) => setAborted(m),
      }, controller.signal).catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Lesson generation could not start.')
        }
      })
    })

    return () => controller.abort()
  }, [state, navigate, addActivity])

  if (!state?.request) {
    return (
      <div className="card">
        <div className="empty">
          <span className="big">📝</span>
          <p>There's no lesson being created right now.</p>
        </div>
        <div className="btn-row">
          <button onClick={() => navigate('/')}>Start a new lesson</button>
        </div>
      </div>
    )
  }

  async function resolveGate(approved: boolean, edited?: unknown) {
    const g = gate
    if (!g || !runIdRef.current) return
    await api.submitGate(runIdRef.current, g.requestId, approved, edited)
    setGate(null)
    addActivity({
      kind: 'review',
      message: approved ? 'Review approved; workflow may continue.' : 'Educator stopped the run.',
    })
    if (!approved) setAborted('You aborted the run.')
  }

  return (
    <div className="card">
      <h2>Building your lesson on “{state.request.topic}”</h2>
      <p className="muted">
        For students around age {state.request.audience_age}. Our team of assistants is at work — this usually
        takes a few minutes. You can keep this tab open and watch the progress below.
      </p>

      <AgentActivity
        connected={runConnected}
        explorePerspectives={state.request.explore_perspectives}
        latestStageId={latestStageId}
        statuses={statuses}
        progressMessage={progressMessage}
        waitingForReview={!!gate}
        error={error}
        aborted={aborted}
        entries={activity}
      />
      <StageTimeline statuses={statuses} explorePerspectives={state.request.explore_perspectives} />

      {error && <p className="error" style={{ marginTop: 16 }}>Error: {error}</p>}
      {aborted && <p style={{ marginTop: 16 }}>{aborted}</p>}
      {(error || aborted) && (
        <div className="btn-row">
          <button onClick={() => navigate('/')}>Start over</button>
        </div>
      )}

      {gate && (
        <GateReview
          key={gate.requestId}
          gate={gate}
          explorePerspectives={state.request.explore_perspectives}
          onApprove={(edited) => resolveGate(true, edited)}
          onAbort={() => resolveGate(false)}
        />
      )}
    </div>
  )
}
