// TypeScript mirrors of the C# DTOs (LessonForge.Core/Dto/LessonData.cs). Field names are
// snake_case to match the API's JSON exactly.

export interface QuizConfig {
  question_count: number
  option_count: number
  difficulty: string // 'auto' | 'easy' | 'medium' | 'hard'
}

export interface LessonRequest {
  topic: string
  audience_age: number
  audience_notes: string
  quiz_config: QuizConfig
}

export interface CitationRef {
  title: string
  url: string
}

export interface GroundedFact {
  statement: string
  citations: CitationRef[]
}

export interface ResearchOutput {
  topic_summary: string
  grounded_facts: GroundedFact[]
  queries_used: string[]
  source_inventory: CitationRef[]
}

export interface LessonSection {
  title: string
  body: string
  activities: string[]
  duration_minutes: number
}

export interface VocabularyTerm {
  term: string
  definition: string
}

export interface LessonPlanOutput {
  title: string
  grade_band: string
  overview: string
  learning_objectives: string[]
  sections: LessonSection[]
  vocabulary: VocabularyTerm[]
  materials: string[]
  citations: CitationRef[]
}

export interface QuizQuestion {
  stem: string
  options: string[]
  correct_index: number
  explanation: string
  citations: CitationRef[]
}

export interface QuizOutput {
  questions: QuizQuestion[]
}

export interface VerificationFlag {
  claim: string
  issue: string
  severity: string
}

export interface VerifiedLessonOutput {
  plan: LessonPlanOutput
  quiz: QuizOutput
  flags: VerificationFlag[]
  citation_coverage: number
  notes: string
}

export interface LessonSummary {
  id: number
  topic: string
  audienceAge: number
  createdAt: string
}

export interface StoredLesson {
  id: number
  topic: string
  audienceAge: number
  createdAt: string
  request: LessonRequest
  result: VerifiedLessonOutput
}

// SSE event payloads from POST /api/lessons/generate.
export type StageStatus = 'started' | 'completed' | 'failed'
export interface RunEvent { runId: string; interactive: boolean }
export interface StageEvent { name: string; status: StageStatus; message?: string }
export interface GateEvent { requestId: string; stage: 'research' | 'plan'; payload: unknown }
export interface CompleteEvent { id: number; result: VerifiedLessonOutput }
