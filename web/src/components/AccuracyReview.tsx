import type { VerificationFlag } from '../types'

const severityOrder = ['High', 'Medium', 'Low', 'Informational'] as const

const severityClass: Record<string, string> = {
  High: 'bad',
  Medium: 'warn',
  Low: 'good',
  Informational: 'neutral',
}

const reviewAction: Record<string, string> = {
  High: 'Review before teaching',
  Medium: 'Educator review recommended',
  Low: 'Minor improvement',
  Informational: 'Optional suggestion',
}

function categoryLabel(category?: string): string {
  if (!category) return 'Uncategorized (older review)'
  return category.replaceAll('_', ' ').replace(/^./, (first) => first.toUpperCase())
}

export default function AccuracyReview({
  flags,
  coverage,
  notes,
  onNavigate,
}: {
  flags: VerificationFlag[]
  coverage: number
  notes: string
  onNavigate: (location: string) => void
}) {
  const percentage = Number.isFinite(coverage) ? Math.round(coverage * 100) : 0

  return (
    <div className="accuracy-review">
      <h3 style={{ marginTop: 0 }}>Accuracy review</h3>
      <p>
        AI-estimated citation coverage: <strong>{percentage}%</strong>
      </p>
      {notes && <p className="muted">{notes}</p>}
      <p className="muted">
        AI findings are advisory, not an exhaustive verification. Inspect the cited sources
        and decide which corrections are appropriate before classroom use.
      </p>

      {flags?.length > 0 ? (
        severityOrder.map((severity) => {
          const bySeverity = flags.filter((flag) => flag.severity === severity)
          if (bySeverity.length === 0) return null
          const categories = [...new Set(bySeverity.map((flag) => flag.category || ''))]
          return (
            <section className="accuracy-group" key={severity}>
              <h4>
                <span className={`pill ${severityClass[severity]}`}>{severity}</span>
                <span className="muted"> {reviewAction[severity]} · {bySeverity.length}</span>
              </h4>
              {categories.map((category) => (
                <div key={category}>
                  <h5>{categoryLabel(category)}</h5>
                  <ul className="accuracy-list">
                    {bySeverity.filter((flag) => (flag.category || '') === category).map((flag, index) => {
                      const navigable = /^(S|Q)[1-9]\d*$/.test(flag.location || '')
                      return (
                        <li key={flag.id || `${flag.claim}-${flag.issue}-${index}`} className="accuracy-finding">
                          <div className="accuracy-finding-header">
                            <span className="muted">{flag.id || 'Earlier review'}</span>
                            {navigable && (
                              <button type="button" className="ghost" onClick={() => onNavigate(flag.location!)}>
                                View {flag.location}
                              </button>
                            )}
                          </div>
                          <p><strong>{flag.claim}</strong> — {flag.issue}</p>
                          {flag.recommendation && (
                            <p><strong>Recommended action:</strong> {flag.recommendation}</p>
                          )}
                          {flag.evidence && (
                            <details>
                              <summary>Evidence and limitations (AI summary)</summary>
                              <p className="muted">{flag.evidence}</p>
                            </details>
                          )}
                        </li>
                      )
                    })}
                  </ul>
                </div>
              ))}
            </section>
          )
        })
      ) : (
        <p className="muted">
          No issues were flagged by the automated review. This does not guarantee factual accuracy.
        </p>
      )}
    </div>
  )
}
