import { useState } from 'react'
import type { GateEvent, LessonPlanOutput, ResearchOutput } from '../types'

interface Props {
  gate: GateEvent
  onApprove: (edited?: unknown) => void
  onAbort: () => void
}

export default function GateReview({ gate, onApprove, onAbort }: Props) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(() => JSON.stringify(gate.payload, null, 2))
  const [parseError, setParseError] = useState<string | null>(null)

  function approve() {
    if (!editing) return onApprove()
    try {
      const edited = JSON.parse(draft)
      setParseError(null)
      onApprove(edited)
    } catch (err) {
      setParseError(err instanceof Error ? err.message : 'Invalid JSON')
    }
  }

  return (
    <div className="backdrop">
      <div className="modal">
        <h2>{gate.stage === 'research' ? 'Review the research' : 'Review the lesson plan'}</h2>
        <p className="muted">
          Take a look before we continue. Approve it as-is, make a few edits first, or stop here.
          Anything you change is used for the next step.
        </p>

        {!editing ? <Summary gate={gate} /> : (
          <>
            <textarea value={draft} onChange={(e) => setDraft(e.target.value)}
              style={{ minHeight: 280, fontFamily: 'monospace', fontSize: 12 }} />
            {parseError && <p className="error">{parseError}</p>}
          </>
        )}

        <div className="btn-row">
          <button onClick={approve}>{editing ? 'Save & approve' : 'Approve'}</button>
          <button className="secondary" onClick={() => setEditing((v) => !v)}>
            {editing ? 'Cancel edit' : 'Edit'}
          </button>
          <button className="danger" onClick={onAbort} style={{ marginLeft: 'auto' }}>Abort run</button>
        </div>
      </div>
    </div>
  )
}

function Summary({ gate }: { gate: GateEvent }) {
  if (gate.stage === 'research') {
    const r = gate.payload as ResearchOutput
    return (
      <div>
        <p>{r.topic_summary}</p>
        <p className="muted">
          {r.grounded_facts?.length ?? 0} grounded facts · {r.source_inventory?.length ?? 0} sources
        </p>
        <ul>
          {r.grounded_facts?.slice(0, 6).map((f, i) => <li key={i}>{f.statement}</li>)}
        </ul>
      </div>
    )
  }
  const p = gate.payload as LessonPlanOutput
  return (
    <div>
      <h3 style={{ marginBottom: 4 }}>{p.title}</h3>
      <p className="muted">{p.grade_band}</p>
      <p>{p.overview}</p>
      <strong>Objectives</strong>
      <ul>{p.learning_objectives?.map((o, i) => <li key={i}>{o}</li>)}</ul>
      <p className="muted">{p.sections?.length ?? 0} sections · {p.vocabulary?.length ?? 0} vocab terms</p>
    </div>
  )
}
