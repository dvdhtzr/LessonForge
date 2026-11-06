import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { LessonRequest } from '../types'

export default function NewLesson() {
  const navigate = useNavigate()
  const [topic, setTopic] = useState('')
  const [age, setAge] = useState(12)
  const [notes, setNotes] = useState('')
  const [questionCount, setQuestionCount] = useState(10)
  const [optionCount, setOptionCount] = useState(4)
  const [difficulty, setDifficulty] = useState('auto')
  const [interactive, setInteractive] = useState(true)

  function submit(e: React.FormEvent) {
    e.preventDefault()
    const request: LessonRequest = {
      topic: topic.trim(),
      audience_age: age,
      audience_notes: notes.trim(),
      quiz_config: { question_count: questionCount, option_count: optionCount, difficulty },
    }
    navigate('/run', { state: { request, interactive } })
  }

  return (
    <form className="card" onSubmit={submit}>
      <h2>Create a lesson plan</h2>
      <p className="muted">
        Enter a topic and target age. LessonForge researches Wikipedia and its cited sources, then a
        crew of agents builds an age-calibrated lesson plan and a citation-backed quiz.
      </p>

      <label htmlFor="topic">Topic or concept</label>
      <input id="topic" value={topic} required placeholder="e.g. Photosynthesis"
        onChange={(e) => setTopic(e.target.value)} />

      <div className="row">
        <div>
          <label htmlFor="age">Target audience age</label>
          <input id="age" type="number" min={4} max={99} value={age}
            onChange={(e) => setAge(Number(e.target.value))} />
        </div>
        <div>
          <label htmlFor="difficulty">Quiz difficulty</label>
          <select id="difficulty" value={difficulty} onChange={(e) => setDifficulty(e.target.value)}>
            <option value="auto">Auto (scale to age)</option>
            <option value="easy">Easy</option>
            <option value="medium">Medium</option>
            <option value="hard">Hard</option>
          </select>
        </div>
      </div>

      <div className="row">
        <div>
          <label htmlFor="qcount">Number of questions</label>
          <input id="qcount" type="number" min={1} max={50} value={questionCount}
            onChange={(e) => setQuestionCount(Number(e.target.value))} />
        </div>
        <div>
          <label htmlFor="ocount">Options per question</label>
          <input id="ocount" type="number" min={2} max={6} value={optionCount}
            onChange={(e) => setOptionCount(Number(e.target.value))} />
        </div>
      </div>

      <label htmlFor="notes">Audience notes (optional)</label>
      <textarea id="notes" value={notes} placeholder="Prior knowledge, setting, accommodations…"
        onChange={(e) => setNotes(e.target.value)} />

      <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontWeight: 400 }}>
        <input type="checkbox" style={{ width: 'auto' }} checked={interactive}
          onChange={(e) => setInteractive(e.target.checked)} />
        Review &amp; approve the research and plan before the quiz is written
      </label>

      <div className="btn-row">
        <button type="submit" disabled={!topic.trim()}>Generate lesson</button>
      </div>
    </form>
  )
}
