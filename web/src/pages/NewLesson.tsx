import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { LessonRequest } from '../types'
import { api, type PerspectiveSignal } from '../api/client'

const EXAMPLES = ['Photosynthesis', 'The water cycle', 'Fractions', 'Ancient Egypt', 'The American Revolution']

export default function NewLesson() {
  const navigate = useNavigate()
  const [topic, setTopic] = useState('')
  const [age, setAge] = useState(12)
  const [notes, setNotes] = useState('')
  const [questionCount, setQuestionCount] = useState(10)
  const [optionCount, setOptionCount] = useState(4)
  const [difficulty, setDifficulty] = useState('auto')
  const [interactive, setInteractive] = useState(true)
  const [explorePerspectives, setExplorePerspectives] = useState(false)
  const [manualPerspectiveOption, setManualPerspectiveOption] = useState(false)
  const [perspectiveSignal, setPerspectiveSignal] = useState<PerspectiveSignal | null>(null)
  const [checkingPerspective, setCheckingPerspective] = useState(false)

  const perspectiveOptionVisible = perspectiveSignal?.suggested === true || manualPerspectiveOption

  function changeTopic(next: string) {
    const normalized = next.trim()
    setTopic(next)
    // A recommendation for one article must never carry over to another.
    setExplorePerspectives(false)
    setManualPerspectiveOption(false)
    setPerspectiveSignal(null)
    // Reset derived UI from the event that caused the change, so the effect
    // below only ever updates state asynchronously (after the debounce/fetch).
    setCheckingPerspective(normalized.length >= 3 && normalized.length <= 120)
  }

  useEffect(() => {
    const normalized = topic.trim()
    if (normalized.length < 3 || normalized.length > 120) return

    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      void api.perspectivesSignal(normalized, controller.signal)
        .then((result) => {
          if (!controller.signal.aborted) setPerspectiveSignal(result)
        })
        .catch(() => {
          if (!controller.signal.aborted) {
            setPerspectiveSignal({
              suggested: false,
              status: 'unavailable',
              reason: 'Article preview unavailable. You can still choose this in Advanced options.',
              sourceTitle: '',
              sourceUrl: '',
            })
          }
        })
        .finally(() => {
          if (!controller.signal.aborted) setCheckingPerspective(false)
        })
    }, 650)

    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [topic])

  function submit(e: React.FormEvent) {
    e.preventDefault()
    const request: LessonRequest = {
      topic: topic.trim(),
      audience_age: age,
      audience_notes: notes.trim(),
      explore_perspectives: perspectiveOptionVisible && explorePerspectives,
      quiz_config: { question_count: questionCount, option_count: optionCount, difficulty },
    }
    navigate('/run', { state: { request, interactive } })
  }

  return (
    <form className="card" onSubmit={submit}>
      <h2>Create a new lesson</h2>
      <p className="muted">
        Tell us what you want to teach and who your students are. LessonForge reads trusted sources,
        writes an age-appropriate lesson plan, and builds a matching quiz — every fact backed by a
        citation you can check.
      </p>

      <label htmlFor="topic">What would you like to teach?</label>
      <input id="topic" value={topic} required placeholder="e.g. Photosynthesis"
        onChange={(e) => changeTopic(e.target.value)} />
      <div className="chips">
        {EXAMPLES.map((ex) => (
          <button type="button" key={ex} className="chip" onClick={() => changeTopic(ex)}>{ex}</button>
        ))}
      </div>

      <div className="row">
        <div>
          <label htmlFor="age">How old are your students?</label>
          <input id="age" type="number" min={4} max={99} value={age}
            onChange={(e) => setAge(Number(e.target.value))} />
          <p className="hint">The reading level and examples are tuned to this age.</p>
        </div>
        <div>
          <label htmlFor="difficulty">Quiz difficulty</label>
          <select id="difficulty" value={difficulty} onChange={(e) => setDifficulty(e.target.value)}>
            <option value="auto">Match the age (recommended)</option>
            <option value="easy">Easy</option>
            <option value="medium">Medium</option>
            <option value="hard">Hard</option>
          </select>
        </div>
      </div>

      <div className="row">
        <div>
          <label htmlFor="qcount">How many quiz questions?</label>
          <input id="qcount" type="number" min={1} max={50} value={questionCount}
            onChange={(e) => setQuestionCount(Number(e.target.value))} />
        </div>
        <div>
          <label htmlFor="ocount">Answer choices per question</label>
          <input id="ocount" type="number" min={2} max={6} value={optionCount}
            onChange={(e) => setOptionCount(Number(e.target.value))} />
        </div>
      </div>

      <label htmlFor="notes">Anything else we should know? (optional)</label>
      <textarea id="notes" value={notes}
        placeholder="e.g. They've already covered cells. Keep it hands-on. Include a group activity."
        onChange={(e) => setNotes(e.target.value)} />

      {checkingPerspective && (
        <p className="hint" role="status">Checking the initial article for evidence of substantive disagreement…</p>
      )}

      {perspectiveSignal?.status === 'unavailable' && !checkingPerspective && (
        <p className="hint" role="status">
          Article-based preview unavailable. Lesson generation remains available;
          use Advanced options to request a perspective review manually.
        </p>
      )}

      {!perspectiveSignal?.suggested && (
        <details style={{ marginTop: 16 }}>
          <summary>Advanced options</summary>
          <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, fontWeight: 400, marginTop: 10 }}>
            <input type="checkbox" style={{ width: 'auto', marginTop: 3 }}
              checked={manualPerspectiveOption}
              onChange={(e) => {
                setManualPerspectiveOption(e.target.checked)
                if (!e.target.checked) setExplorePerspectives(false)
              }} />
            <span>Allow me to explore differing perspectives even without a source-based recommendation.</span>
          </label>
        </details>
      )}

      {perspectiveOptionVisible && (
        <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, fontWeight: 400, marginTop: 20 }}>
          <input type="checkbox" style={{ width: 'auto', marginTop: 3 }} checked={explorePerspectives}
            onChange={(e) => setExplorePerspectives(e.target.checked)} />
          <span>
            <strong>Explore differing perspectives</strong>
            <span className="hint" style={{ display: 'block' }}>
              {perspectiveSignal?.suggested
                ? 'The initial Wikipedia article contains multiple signals of scholarly, ethical, or methodological dispute.'
                : 'Manually selected: the Perspective Analyst will assess whether the evidence supports differing interpretations.'}
              {' '}The analyst checks the evidence and does not invent debate or treat unsupported views as equal.
              With educator review enabled, you can approve or edit its findings.
            </span>
            {perspectiveSignal?.suggested && perspectiveSignal.sourceUrl.startsWith('https://en.wikipedia.org/wiki/') && (
              <span className="hint" style={{ display: 'block' }}>
                Initial article: <a href={perspectiveSignal.sourceUrl}
                  target="_blank" rel="noopener noreferrer">{perspectiveSignal.sourceTitle}</a>
              </span>
            )}
          </span>
        </label>
      )}

      <label style={{ display: 'flex', alignItems: 'flex-start', gap: 10, fontWeight: 400, marginTop: 20 }}>
        <input type="checkbox" style={{ width: 'auto', marginTop: 3 }} checked={interactive}
          onChange={(e) => setInteractive(e.target.checked)} />
        <span>
          Let me review the research and the plan before the quiz is written.
          <span className="hint" style={{ display: 'block' }}>
            You'll get a chance to approve or tweak each step. Uncheck to let LessonForge do it all in one go.
          </span>
        </span>
      </label>

      <div className="btn-row">
        <button type="submit" disabled={!topic.trim()}>✨ Generate my lesson</button>
      </div>
    </form>
  )
}
