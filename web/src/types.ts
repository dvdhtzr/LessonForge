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
  explore_perspectives: boolean
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

export interface PerspectiveViewpoint {
  name: string
  interpretation: string
  evidence_basis: string
  limitations: string
  citations: CitationRef[]
}

export type PerspectiveStatus =
  | 'supported_disagreement'
  | 'no_substantive_disagreement'
  | 'insufficient_evidence'

export interface PerspectiveAnalysisOutput {
  status: PerspectiveStatus
  perspectives: PerspectiveViewpoint[]
  areas_of_agreement: string[]
  evidence_limitations: string
  discussion_questions: string[]
}

export interface ResearchOutput {
  topic_summary: string
  grounded_facts: GroundedFact[]
  queries_used: string[]
  source_inventory: CitationRef[]
  perspectives?: PerspectiveAnalysisOutput | null
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
  // New reviews have host-assigned IDs and structured metadata. Older saved
  // lessons retain only claim/issue/severity and must remain readable.
  id?: string
  claim: string
  issue: string
  severity: string
  category?: string
  location?: string
  recommendation?: string
  evidence?: string
}

export interface VerifiedLessonOutput {
  plan: LessonPlanOutput
  quiz: QuizOutput
  perspectives?: PerspectiveAnalysisOutput | null
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
