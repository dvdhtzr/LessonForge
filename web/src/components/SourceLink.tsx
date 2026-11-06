import type { CitationRef } from '../types'

type SourceKind = 'wikipedia' | 'usgs' | 'loc' | 'gutenberg' | 'smithsonian' | 'other'

const SOURCE_TYPES: Record<SourceKind, { mark: string; name: string; category: string }> = {
  wikipedia: { mark: 'W', name: 'Wikipedia', category: 'Article' },
  usgs: { mark: 'USGS', name: 'USGS', category: 'Earthquake observation' },
  loc: { mark: 'LOC', name: 'Library of Congress', category: 'Catalog record' },
  gutenberg: { mark: 'PG', name: 'Project Gutenberg', category: 'Book catalog record' },
  smithsonian: { mark: 'SI', name: 'Smithsonian Open Access', category: 'Museum catalog record' },
  other: { mark: 'DOC', name: 'Source', category: 'Source document' },
}

/**
 * Visual source identity only. The rights gate and saved source_records are
 * authoritative for provenance; this display helper does not grant rights.
 * No remote favicons, article images or other unreviewed media are fetched.
 */
function identifySource(url: string, providerId?: string): SourceKind {
  switch (providerId) {
    case 'wikipedia-text': return 'wikipedia'
    case 'usgs-earthquakes': return 'usgs'
    case 'loc-century-of-lawmaking': return 'loc'
    case 'gutenberg-catalog': return 'gutenberg'
    case 'smithsonian-openaccess-metadata': return 'smithsonian'
  }

  try {
    const host = new URL(url).hostname.toLowerCase()
    if (host === 'en.wikipedia.org') return 'wikipedia'
    if (host === 'earthquake.usgs.gov') return 'usgs'
    if (host === 'www.loc.gov') return 'loc'
    if (host === 'www.gutenberg.org') return 'gutenberg'
    if (host === 'collections.si.edu') return 'smithsonian'
  } catch {
    // Display invalid citations as text rather than creating a link.
  }
  return 'other'
}

interface Props {
  citation: CitationRef
  providerId?: string
  compact?: boolean
}

export default function SourceLink({ citation, providerId, compact = false }: Props) {
  const kind = identifySource(citation.url, providerId)
  const source = SOURCE_TYPES[kind]
  let safe = false
  try {
    safe = ['https:', 'http:'].includes(new URL(citation.url).protocol)
  } catch {
    // Never render an unsafe URL as an actionable link.
  }

  const content = (
    <>
      <span className={`source-mark source-mark--${kind}`} aria-hidden="true">{source.mark}</span>
      <span className="source-link__text">
        <span className="source-link__title">{citation.title?.trim() || citation.url || 'Untitled source'}</span>
        {!compact && <span className="source-kind">{source.name} · {source.category}</span>}
      </span>
    </>
  )

  if (!safe) {
    return <span className="source-link source-link--invalid">{content}</span>
  }
  return (
    <a className={`source-link ${compact ? 'source-link--compact' : ''}`}
      href={citation.url} target="_blank" rel="noopener noreferrer"
      aria-label={`${citation.title?.trim() || citation.url} — ${source.name} (opens in a new tab)`}>
      {content}
    </a>
  )
}
