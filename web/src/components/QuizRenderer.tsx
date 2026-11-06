import type { QuizOutput } from '../types'

export default function QuizRenderer({ quiz, mode }: { quiz: QuizOutput; mode: 'student' | 'teacher' }) {
  return (
    <div>
      {quiz.questions?.map((q, i) => (
        <div className="quiz-q" key={i} id={`lesson-q${i + 1}`}>
          <strong>{i + 1}. {q.stem}</strong>
          <ol type="A">
            {q.options.map((opt, o) => (
              <li key={o} className={mode === 'teacher' && o === q.correct_index ? 'correct' : ''}>
                {opt}{mode === 'teacher' && o === q.correct_index ? '  ✓' : ''}
              </li>
            ))}
          </ol>
          {mode === 'teacher' && q.explanation && (
            <div className="explain">
              <strong>Why:</strong> {q.explanation}
              {q.citations?.length > 0 && (
                <div>
                  <strong>Sources:</strong>{' '}
                  {q.citations.map((c, k) => (
                    <span key={k}>
                      <a href={c.url} target="_blank" rel="noreferrer">{c.title || c.url}</a>
                      {k < q.citations.length - 1 ? '; ' : ''}
                    </span>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      ))}
    </div>
  )
}
