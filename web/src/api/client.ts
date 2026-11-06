import type {
  CompleteEvent, GateEvent, LessonRequest, LessonSummary, RunEvent, StageEvent, StoredLesson,
} from '../types'

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

export const api = {
  list: () => fetch('/api/lessons').then(json<LessonSummary[]>),
  get: (id: number) => fetch(`/api/lessons/${id}`).then(json<StoredLesson>),

  submitGate: (runId: string, requestId: string, approved: boolean, payload?: unknown) =>
    fetch(`/api/lessons/gate/${runId}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ requestId, approved, payload }),
    }),

  exportUrl: (id: number, format: 'md' | 'json' | 'pdf', audience: 'student' | 'teacher') =>
    `/api/lessons/${id}/export?format=${format}&audience=${audience}`,
}

export interface GenerateHandlers {
  onRun?: (e: RunEvent) => void
  onStage?: (e: StageEvent) => void
  onGate?: (e: GateEvent) => void
  onComplete?: (e: CompleteEvent) => void
  onError?: (message: string) => void
  onAborted?: (message: string) => void
}

/**
 * Starts a generation run and parses the Server-Sent-Events stream. EventSource can't POST a body,
 * so we read the fetch response stream and split it into SSE frames by hand.
 */
export async function generate(
  request: LessonRequest,
  interactive: boolean,
  handlers: GenerateHandlers,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(`/api/lessons/generate?interactive=${interactive}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!res.ok || !res.body) throw new Error(`generate failed: ${res.status}`)

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let sep: number
    while ((sep = buffer.indexOf('\n\n')) !== -1) {
      const frame = buffer.slice(0, sep)
      buffer = buffer.slice(sep + 2)
      dispatch(frame, handlers)
    }
  }
}

function dispatch(frame: string, h: GenerateHandlers) {
  let event = 'message'
  const dataLines: string[] = []
  for (const line of frame.split('\n')) {
    if (line.startsWith('event:')) event = line.slice(6).trim()
    else if (line.startsWith('data:')) dataLines.push(line.slice(5).trim())
  }
  if (dataLines.length === 0) return
  const data = JSON.parse(dataLines.join('\n'))

  switch (event) {
    case 'run': h.onRun?.(data); break
    case 'stage': h.onStage?.(data); break
    case 'gate': h.onGate?.(data); break
    case 'complete': h.onComplete?.(data); break
    case 'error': h.onError?.(data.message ?? 'Unknown error'); break
    case 'aborted': h.onAborted?.(data.message ?? 'Run aborted'); break
    case 'done': break
  }
}
