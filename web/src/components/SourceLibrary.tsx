import type { SourceEvidenceRecord } from '../types'
import SourceLink from './SourceLink'

/**
 * Teacher-facing, collapsible inventory of the exact materials admitted to
 * the lesson's research index. This is provenance, not a claim that every
 * record supports every lesson statement.
 */
export default function SourceLibrary({ records }: { records: SourceEvidenceRecord[] }) {
  if (!records?.length) return null

  return (
    <div className="card">
      <details className="source-library">
        <summary>
          <span>Research source library</span>
          <span className="pill neutral">{records.length} retrieved sources</span>
        </summary>
        <p className="muted">
          Sources available to the research pipeline. A source appearing here does not
          necessarily mean it supports every claim in the lesson.
        </p>
        <ul className="source-list">
          {records.map((record, index) => (
            <li key={`${record.provider_id}:${record.canonical_id}:${index}`}>
              <SourceLink providerId={record.provider_id}
                citation={{ title: record.title, url: record.url }} />
              <div className="source-library__meta">
                <span>Rights: {record.license_id || 'Unspecified'}</span>
                {record.attribution && <span>{record.attribution}</span>}
              </div>
            </li>
          ))}
        </ul>
      </details>
    </div>
  )
}
