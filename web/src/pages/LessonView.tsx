import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import PlanRenderer from '../components/PlanRenderer'
import QuizRenderer from '../components/QuizRenderer'
import PerspectivePanel from '../components/PerspectivePanel'
import AccuracyReview from '../components/AccuracyReview'

type Tab = 'plan' | 'quiz'

export default function LessonView() {
  const { id } = useParams()
  const lessonId = Number(id)
  const [tab, setTab] = useState<Tab>('plan')
  const [audience, setAudience] = useState<'student' | 'teacher'>('teacher')

  const { data, isLoading, error } = useQuery({
    queryKey: ['lesson', lessonId],
    queryFn: () => api.get(lessonId),
  })

  if (isLoading) return <div className="card">Loading…</div>
  if (error || !data) return <div className="card error">Could not load this lesson.</div>

  const { result } = data

  return (
    <>
      <div className="card">
        <div className="tabs">
          <button className={tab === 'plan' ? 'active' : ''} onClick={() => setTab('plan')}>Lesson plan</button>
          <button className={tab === 'quiz' ? 'active' : ''} onClick={() => setTab('quiz')}>Quiz</button>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center' }}>
            <span className="muted">View</span>
            <select value={audience} onChange={(e) => setAudience(e.target.value as 'student' | 'teacher')}
              style={{ width: 'auto' }}>
              <option value="teacher">Teacher</option>
              <option value="student">Student</option>
            </select>
          </div>
        </div>

        {tab === 'plan'
          ? <PlanRenderer plan={result.plan} />
          : <QuizRenderer quiz={result.quiz} mode={audience} />}
      </div>

      {result.perspectives && (
        <div className="card">
          <PerspectivePanel analysis={result.perspectives} />
        </div>
      )}

      {audience === 'teacher' && (
        <div className="card">
          <AccuracyReview
          flags={result.flags || []}
          coverage={result.citation_coverage ?? 0}
          notes={result.notes || ''}
          onNavigate={(location) => {
            setTab(location.startsWith('Q') ? 'quiz' : 'plan')
            window.requestAnimationFrame(() => {
              document.getElementById(`lesson-${location.toLowerCase()}`)
                ?.scrollIntoView({ behavior: 'smooth', block: 'center' })
            })
          }}
          />
        </div>
      )}

      <div className="card">
        <h3 style={{ marginTop: 0 }}>Export</h3>
        <p className="muted">
          Teacher copies include answers, explanations and sources; student copies omit them.
        </p>
        <div className="btn-row">
          {(['md', 'json', 'pdf'] as const).map((fmt) => (
            <a key={fmt} href={api.exportUrl(lessonId, fmt, audience)}>
              <button className="secondary">Download {fmt.toUpperCase()} ({audience})</button>
            </a>
          ))}
        </div>
      </div>
    </>
  )
}
