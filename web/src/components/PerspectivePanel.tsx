import type { CitationRef, PerspectiveAnalysisOutput } from '../types'
import SourceLink from './SourceLink'

function Sources({ citations }: { citations: CitationRef[] }) {
  if (!Array.isArray(citations) || citations.length === 0) return <span className="error">No source provided</span>
  return (
    <span>
      {citations.map((source, index) => (
        <span key={index}>
          {index > 0 ? ' · ' : ''}
          <SourceLink citation={source} compact />
        </span>
      ))}
    </span>
  )
}

/** Displays approved interpretive evidence, including an explicit no-debate conclusion. */
export default function PerspectivePanel({ analysis }: { analysis: PerspectiveAnalysisOutput }) {
  const supported = analysis.status === 'supported_disagreement'
  const label = supported
    ? 'Credible differing interpretations'
    : analysis.status === 'no_substantive_disagreement'
      ? 'No substantive disagreement established'
      : 'Evidence insufficient to compare perspectives'

  return (
    <section className="perspective-panel">
      <h3>Perspective analysis</h3>
      <p><strong>{label}</strong></p>
      {supported && (
        <ol>
          {analysis.perspectives.map((view, index) => (
            <li key={index}>
              <strong>{view.name}</strong>
              <p>{view.interpretation}</p>
              <p><strong>Supporting evidence:</strong> {view.evidence_basis}</p>
              <p className="muted"><strong>Limitations:</strong> {view.limitations}</p>
              <p className="muted"><strong>Sources:</strong> <Sources citations={view.citations} /></p>
            </li>
          ))}
        </ol>
      )}

      {analysis.areas_of_agreement?.length > 0 && (
        <>
          <h4>Areas of agreement</h4>
          <ul>{analysis.areas_of_agreement.map((point, i) => <li key={i}>{point}</li>)}</ul>
        </>
      )}
      <p className="muted">
        <strong>Evidence limitations:</strong> {analysis.evidence_limitations}
      </p>

      {analysis.discussion_questions?.length > 0 && (
        <>
          <h4>Discussion and source-evaluation questions</h4>
          <ul>{analysis.discussion_questions.map((question, i) => <li key={i}>{question}</li>)}</ul>
        </>
      )}
      <p className="hint">Different interpretations are considered in proportion to their evidence, not treated as equally supported by default.</p>
    </section>
  )
}
