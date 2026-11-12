import test from 'node:test'
import assert from 'node:assert/strict'
import {assess,parseFrame,argumentsFor,summary,uniquenessMetrics,collectTrace} from './benchmark-small-models.mjs'
import {mkdtemp,mkdir,writeFile,rm} from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const request={quiz_config:{question_count:1,option_count:2}}
function lesson(){
 return {result:{
  plan:{sections:[{title:'A'}],learning_objectives:['Learn'],citations:[{url:'https://example.org/article'}]},
  quiz:{questions:[{stem:'Question?',options:['A','B'],correct_index:0,
   explanation:'Answer explanation',citations:[{url:'https://example.org/article'}]}]},
  source_records:[{url:'https://example.org/article',provider_id:'fixture',document_sha256:'abc',
   license_id:'CC0',policy_url:'https://example.org/policy'}],
  flags:[],notes:'Bounded audit completed.',citation_coverage:0.8
 }}
}
test('valid persisted output satisfies structural checks',()=>{
 assert.equal(assess(lesson(),request).pass,true)
})
test('invented citation URL fails',()=>{
 const l=lesson();l.result.quiz.questions[0].citations=[{url:'https://example.org/fabricated'}]
 assert.equal(assess(l,request).pass,false)
 assert.match(assess(l,request).issues.join('; '),/Unrecognized citation/)
})
test('wrong answer index and missing accuracy review fail',()=>{
 const l=lesson();l.result.quiz.questions[0].correct_index=2;l.result.notes=''
 assert.equal(assess(l,request).pass,false)
})
test('missing question count and source provenance fail',()=>{
 const l=lesson();l.result.quiz.questions=[];l.result.source_records=[]
 assert.equal(assess(l,request).pass,false)
})
test('SSE stage parser accepts data across lines',()=>{
 assert.deepEqual(parseFrame('event: stage\ndata: {"name":"Research",\ndata: "status":"started"}'),
  {event:'stage',data:{name:'Research',status:'started'}})
})
test('arguments reject invalid suites, models and ports',()=>{
 assert.throws(()=>argumentsFor(['--suite','unrecognized']),/suite/)
 assert.throws(()=>argumentsFor(['--models','not-a-tag']),/model:tag/)
 assert.throws(()=>argumentsFor(['--port','80']),/port/)
})
test('summary labels the limit of structural validation',()=>{
 const text=summary({runs:[]})
 assert.match(text,/not evidence of factual accuracy/)
})

test('verifier telemetry measures repairs but never claims educational accuracy',()=>{
 const events=[
  {event:'progress',data:{stage:'Research',message:'ignored'}},
  {event:'progress',data:{stage:'AnswerUniqueness',questionNumber:1,status:'accepted'}},
  {event:'progress',data:{stage:'AnswerUniqueness',questionNumber:2,status:'repair_requested'}},
  {event:'progress',data:{stage:'AnswerUniqueness',questionNumber:2,status:'repaired'}}
 ]
 assert.deepEqual(uniquenessMetrics(events),
  {verified:2,repairAttempts:1,repairsAccepted:1,unresolved:0})
 const text=summary({runs:[{model:'qwen3:4b',caseId:'science',
  status:'STRUCTURAL_PASS',seconds:90,assessment:{},uniqueness:uniquenessMetrics(events)}]})
 assert.match(text,/Repair attempts/)
 assert.match(text,/2 \| 1 \| 1/)
 assert.equal(uniquenessMetrics([]),null)
})

test('failed-run diagnostics retain per-option evidence and repair events',async()=>{
 const output=await mkdtemp(path.join(os.tmpdir(),'lf-diagnostics-test-'))
 try{
  const dir=path.join(output,'diagnostics')
  await mkdir(dir)
  const runId='abcdef123456'
  const events=[{event:'run',data:{runId}},{event:'error',data:{message:'Failed'}}]
  const records=[
   {schemaVersion:1,runId,sequence:1,diagnostic:{kind:'review_started',
    questionNumber:8,attempt:'original',question:{stem:'Which birthplace?',options:['Solano','Calabria']}}},
   {schemaVersion:1,runId,sequence:2,diagnostic:{kind:'review_result',
    questionNumber:8,attempt:'original',review:{option_verdicts:[
     {index:0,verdict:'defensible'},{index:1,verdict:'defensible'}]}}},
   {schemaVersion:1,runId,sequence:3,diagnostic:{kind:'failure',
    questionNumber:8,attempt:'replacement',failureKind:'review_invalid_or_unavailable'}}
  ]
  await writeFile(path.join(dir,runId+'.answer-uniqueness.jsonl'),
   records.map(x=>JSON.stringify(x)).join('\n')+'\n')
  const got=await collectTrace(output,dir,'qwen3:4b','named-subject',events)
  assert.equal(got.records.length,3)
  assert.match(got.diagnosticFile,/diagnostics/)
  assert.equal(got.records[0].diagnostic.question.stem,'Which birthplace?')
  assert.equal(got.records[1].diagnostic.review.option_verdicts[1].verdict,'defensible')
  assert.equal(got.records[2].diagnostic.kind,'failure')
  assert.deepEqual((await collectTrace(output,dir,'qwen3:4b','science',[])).records,[])
 }finally{
  await rm(output,{recursive:true,force:true})
 }
})
