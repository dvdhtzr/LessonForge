// Shared immutable stage metadata for the workflow UI.
// Kept separate from React components so Fast Refresh can safely reload them.

export const STAGES = [
  { id: 'Research', role: 'Research Librarian', label: 'Gathering and citing sources', mark: 'R' },
  { id: 'PerspectiveAnalysis', role: 'Perspective Analyst', label: 'Comparing evidence and interpretations', mark: 'P' },
  { id: 'LessonPlanner', role: 'Curriculum Designer', label: 'Designing the lesson', mark: 'C' },
  { id: 'QuizGenerator', role: 'Assessment Writer', label: 'Writing the quiz', mark: 'A' },
  { id: 'AnswerUniqueness', role: 'Answer Verifier', label: 'Checking each answer choice', mark: 'V' },
  { id: 'FactCheck', role: 'Accuracy Editor', label: 'Checking claims and citations', mark: 'E' },
] as const

export function getStage(id: string | null | undefined) {
  return STAGES.find((stage) => stage.id === id)
}
