import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'

export default function History() {
  const { data, isLoading, error } = useQuery({ queryKey: ['lessons'], queryFn: api.list })

  if (isLoading) return <div className="card">Loading…</div>
  if (error) return <div className="card error">Could not load history.</div>

  return (
    <div className="card">
      <h2>My lessons</h2>
      {!data || data.length === 0 ? (
        <div className="empty">
          <span className="big">🍎</span>
          <p>You haven't created any lessons yet.<br />Head to <strong>New lesson</strong> to make your first one.</p>
        </div>
      ) : (
        <ul className="summaries">
          {data.map((l) => (
            <li key={l.id}>
              <Link to={`/lessons/${l.id}`}>{l.topic}</Link>
              <span className="muted">age {l.audienceAge}</span>
              <span className="muted" style={{ marginLeft: 'auto' }}>
                {new Date(l.createdAt).toLocaleString()}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
