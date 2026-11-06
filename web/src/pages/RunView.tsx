import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { api, generate } from '../api/client'
import type { GateEvent, LessonRequest, StageStatus } from '../types'
import StageTimeline from '../components/StageTimeline'
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
  const runIdRef = useRef<string | null>(null)

  useEffect(() => {
    if (!state?.request) return
    const controller = new AbortController()

    // StrictMode replays effects in development. Defer the POST until its first
    // cleanup has had a chance to abort, so we start exactly one workflow.
    queueMicrotask(() => {
      if (controller.signal.aborted) return
      void generate(state.request, state.interactive, {
        onRun: (e) => { runIdRef.current = e.runId; setRunConnected(true) },
        onStage: (e) => {
          setStatuses((prev) => ({ ...prev, [e.name]: e.status }))
          if (e.name !== 'Research' || e.status !== 'started') setProgressMessage(null)
        },
        onProgress: (e) => setProgressMessage(e.message),
        onGate: (e) => setGate(e),
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
  }, [state, navigate])

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
    if (!approved) setAborted('You aborted the run.')
  }

  return (
    <div className="card">
      <h2>Building your lesson on “{state.request.topic}”</h2>
      <p className="muted">
        For students around age {state.request.audience_age}. Our team of assistants is at work — this usually
        takes a few minutes. You can keep this tab open and watch the progress below.
      </p>

      {Object.keys(statuses).length === 0 && !error && !aborted && (
        <p className="muted" role="status">
          {runConnected ? 'Connected — preparing the research stage…' : 'Connecting to the lesson service…'}
        </p>
      )}
      <StageTimeline statuses={statuses} explorePerspectives={state.request.explore_perspectives} />
      {progressMessage && !error && !aborted && (
        <p className="muted" role="status" aria-live="polite">{progressMessage}</p>
      )}

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
          onApprove={(edited) => resolveGate(true, edited)}
          onAbort={() => resolveGate(false)}
        />
      )}
    </div>
  )
}
