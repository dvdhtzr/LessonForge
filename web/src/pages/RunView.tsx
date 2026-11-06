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
  const runIdRef = useRef<string | null>(null)
  const started = useRef(false)

  useEffect(() => {
    if (!state?.request || started.current) return
    started.current = true
    const controller = new AbortController()

    generate(state.request, state.interactive, {
      onRun: (e) => { runIdRef.current = e.runId },
      onStage: (e) => setStatuses((prev) => ({ ...prev, [e.name]: e.status })),
      onGate: (e) => setGate(e),
      onComplete: (e) => navigate(`/lessons/${e.id}`, { state: { lessonId: e.id } }),
      onError: (m) => setError(m),
      onAborted: (m) => setAborted(m),
    }, controller.signal).catch((err) => {
      if (!controller.signal.aborted) setError(err.message)
    })

    return () => controller.abort()
  }, [state, navigate])

  if (!state?.request) {
    return (
      <div className="card">
        <p>No run in progress.</p>
        <button onClick={() => navigate('/')}>Start a new lesson</button>
      </div>
    )
  }

  async function resolveGate(approved: boolean, edited?: unknown) {
    const g = gate
    if (!g || !runIdRef.current) return
    setGate(null)
    await api.submitGate(runIdRef.current, g.requestId, approved, edited)
    if (!approved) setAborted('You aborted the run.')
  }

  return (
    <div className="card">
      <h2>Generating “{state.request.topic}”</h2>
      <p className="muted">Target age {state.request.audience_age}. This can take a few minutes on a local model.</p>

      <StageTimeline statuses={statuses} />

      {error && <p className="error" style={{ marginTop: 16 }}>Error: {error}</p>}
      {aborted && <p style={{ marginTop: 16 }}>{aborted}</p>}
      {(error || aborted) && (
        <div className="btn-row">
          <button onClick={() => navigate('/')}>Start over</button>
        </div>
      )}

      {gate && (
        <GateReview
          gate={gate}
          onApprove={(edited) => resolveGate(true, edited)}
          onAbort={() => resolveGate(false)}
        />
      )}
    </div>
  )
}
