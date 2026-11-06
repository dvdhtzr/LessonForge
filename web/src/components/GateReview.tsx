import { useState } from 'react'
import type { CitationRef, GateEvent, GroundedFact, LessonPlanOutput, ResearchOutput } from '../types'
import PerspectivePanel from './PerspectivePanel'

interface Props {
  gate: GateEvent
  onApprove: (edited?: unknown) => Promise<void>
  onAbort: () => Promise<void>
  explorePerspectives?: boolean
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function researchValidationError(value: unknown, requirePerspectives: boolean): string | null {
  if (!isObject(value) || !Array.isArray(value.grounded_facts) || value.grounded_facts.length === 0) {
    return 'The research contains no readable grounded facts. Inspect the JSON before approving.'
  }
  if (typeof value.topic_summary !== 'string' || !value.topic_summary.trim()) {
    return 'The research summary is missing.'
  }

  for (const [index, fact] of value.grounded_facts.entries()) {
    if (!isObject(fact) || typeof fact.statement !== 'string' || !fact.statement.trim()) {
      return `Fact ${index + 1} has no statement. Inspect or correct the JSON before approving.`
    }
    if (
      !Array.isArray(fact.citations) || fact.citations.length === 0 ||
      fact.citations.some((citation: unknown) =>
        !isObject(citation) ||
        typeof citation.title !== 'string' || !citation.title.trim() ||
        typeof citation.url !== 'string' || !/^https?:\/\//i.test(citation.url)
      )
    ) {
      return `Fact ${index + 1} has missing or invalid citations. Inspect the JSON before approving.`
    }
  }

  const analysis = value.perspectives
  if (requirePerspectives && (!isObject(analysis))) {
    return 'The Perspective Analyst did not provide a review. Inspect the JSON before approving.'
  }
  if (analysis != null) {
    if (!isObject(analysis)) return 'Perspective analysis must be an object.'
    const status = analysis.status
    if (!['supported_disagreement', 'no_substantive_disagreement', 'insufficient_evidence'].includes(String(status))) {
      return 'Perspective analysis has an unsupported status.'
    }
    if (typeof analysis.evidence_limitations !== 'string' || !analysis.evidence_limitations.trim()) {
      return 'Perspective analysis must describe its evidence limitations.'
    }
    if (!Array.isArray(analysis.perspectives)) return 'Perspective analysis is missing its perspectives array.'
    if (!Array.isArray(analysis.areas_of_agreement) || !Array.isArray(analysis.discussion_questions)) {
      return 'Perspective analysis is missing agreement or discussion-question arrays.'
    }
    const supported = status === 'supported_disagreement'
    if (supported && (analysis.perspectives.length < 2 || analysis.perspectives.length > 3)) {
      return 'Supported disagreement requires 2–3 sourced interpretations.'
    }
    if (!supported && analysis.perspectives.length > 0) {
      return 'Do not include competing interpretations when evidence has not established disagreement.'
    }
    const knownUrls = new Set(
      Array.isArray(value.source_inventory)
        ? value.source_inventory.filter(isObject).map((source) => source.url)
        : [],
    )
    for (const [index, perspective] of analysis.perspectives.entries()) {
      if (!isObject(perspective) ||
        ['name', 'interpretation', 'evidence_basis', 'limitations'].some(
          (field) => typeof perspective[field] !== 'string' || !(perspective[field] as string).trim()
        )) {
        return `Interpretation ${index + 1} is missing its analysis or limitations.`
      }
      if (!Array.isArray(perspective.citations) || perspective.citations.length === 0 ||
        perspective.citations.some((source: unknown) =>
          !isObject(source) ||
          typeof source.title !== 'string' || !source.title.trim() ||
          typeof source.url !== 'string' || !knownUrls.has(source.url)
        )) {
        return `Interpretation ${index + 1} must cite a retrieved source.`
      }
    }
  }
  return null
}

function reviewValidationError(gate: GateEvent, value: unknown, explorePerspectives: boolean): string | null {
  return gate.stage === 'research' ? researchValidationError(value, explorePerspectives) : null
}

export default function GateReview({ gate, onApprove, onAbort, explorePerspectives = false }: Props) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(() => JSON.stringify(gate.payload, null, 2))
  const [parseError, setParseError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const reviewError = reviewValidationError(gate, gate.payload, explorePerspectives)

  async function approve() {
    let edited: unknown = undefined

    if (editing) {
      try {
        edited = JSON.parse(draft)
      } catch (err) {
        setParseError(err instanceof Error ? err.message : 'Invalid JSON')
        return
      }
    }

    const validationError = reviewValidationError(gate, editing ? edited : gate.payload, explorePerspectives)
    if (validationError) {
      setParseError(validationError)
      return
    }

    setParseError(null)
    setSubmitting(true)
    try {
      await onApprove(edited)
    } catch (err) {
      setParseError(err instanceof Error ? err.message : 'Could not submit review decision.')
      setSubmitting(false)
    }
  }

  async function abort() {
    setSubmitting(true)
    try {
      await onAbort()
    } catch (err) {
      setParseError(err instanceof Error ? err.message : 'Could not abort the run.')
      setSubmitting(false)
    }
  }

  return (
    <div className="backdrop">
      <div className="modal review-modal" role="dialog" aria-modal="true" aria-labelledby="review-title">
        <h2 id="review-title">{gate.stage === 'research' ? 'Review the research' : 'Review the lesson plan'}</h2>
        <p className="muted">
          Review the material and supporting sources before continuing. Approve it as-is, edit the JSON,
          or stop the run. Approved edits are passed to the next stage.
        </p>

        {!editing ? <Summary gate={gate} /> : (
          <>
            <label htmlFor="review-json">Review payload (JSON)</label>
            <textarea id="review-json" value={draft}
              onChange={(e) => { setDraft(e.target.value); setParseError(null) }}
              className="review-json" spellCheck={false} />
          </>
        )}

        {!editing && reviewError && (
          <p className="error" role="alert">{reviewError} Use Edit to inspect the received payload.</p>
        )}
        {parseError && <p className="error" role="alert">{parseError}</p>}

        <div className="btn-row">
          <button onClick={() => void approve()} disabled={submitting || (!editing && !!reviewError)}>
            {submitting ? 'Submitting…' : editing ? 'Save & approve' : 'Approve'}
          </button>
          <button className="secondary" disabled={submitting}
            onClick={() => { setEditing((v) => !v); setParseError(null) }}>
            {editing ? 'Cancel edit' : 'Edit'}
          </button>
          <button className="danger" disabled={submitting} onClick={() => void abort()} style={{ marginLeft: 'auto' }}>
            Abort run
          </button>
        </div>
      </div>
    </div>
  )
}

function CitationLinks({ citations }: { citations?: CitationRef[] }) {
  if (!Array.isArray(citations) || citations.length === 0) {
    return <span className="error">No citations supplied</span>
  }

  return (
    <span className="review-citations">
      Sources: {citations.map((citation, index) => (
        <span key={index}>
          {index > 0 && ' · '}
          {/^https?:\/\//i.test(citation.url ?? '')
            ? <a href={citation.url} target="_blank" rel="noopener noreferrer">
                {citation.title?.trim() || citation.url}
              </a>
            : <span className="error">Invalid source URL</span>}
        </span>
      ))}
    </span>
  )
}

function ResearchSummary({ research }: { research: ResearchOutput }) {
  const facts = Array.isArray(research.grounded_facts) ? research.grounded_facts : []
  const sources = Array.isArray(research.source_inventory) ? research.source_inventory : []

  return (
    <div className="review-summary">
      <p>{research.topic_summary || 'No research summary provided.'}</p>
      <p className="muted">
        {facts.length} grounded facts · {sources.length} retrieved sources
      </p>

      <h3>Grounded facts</h3>
      <ol className="review-facts">
        {facts.map((fact: GroundedFact, index) => (
          <li key={index}>
            <div className={typeof fact?.statement === 'string' && fact.statement.trim() ? '' : 'error'}>
              {typeof fact?.statement === 'string' && fact.statement.trim()
                ? fact.statement
                : 'Missing fact statement — inspect the JSON before approving.'}
            </div>
            <CitationLinks citations={fact?.citations} />
          </li>
        ))}
      </ol>

      {research.perspectives && <PerspectivePanel analysis={research.perspectives} />}

      {sources.length > 0 && (
        <details className="review-inventory">
          <summary>View all {sources.length} retrieved sources</summary>
          <ul>
            {sources.map((source, index) => (
              <li key={index}><CitationLinks citations={[source]} /></li>
            ))}
          </ul>
        </details>
      )}
    </div>
  )
}

function Summary({ gate }: { gate: GateEvent }) {
  if (gate.stage === 'research') {
    return <ResearchSummary research={gate.payload as ResearchOutput} />
  }

  const plan = gate.payload as LessonPlanOutput
  return (
    <div className="review-summary">
      <h3>{plan.title}</h3>
      <p className="muted">{plan.grade_band}</p>
      <p>{plan.overview}</p>
      <strong>Learning objectives</strong>
      <ol>{plan.learning_objectives?.map((objective, index) => <li key={index}>{objective}</li>)}</ol>
      <strong>Lesson sections</strong>
      <ol>{plan.sections?.map((section, index) => (
        <li key={index}>{section.title} ({section.duration_minutes} minutes)</li>
      ))}</ol>
      <p className="muted">{plan.sections?.length ?? 0} sections · {plan.vocabulary?.length ?? 0} vocabulary terms</p>
      <CitationLinks citations={plan.citations} />
    </div>
  )
}
