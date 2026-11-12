import test from 'node:test'
import assert from 'node:assert/strict'
import {assess,parseFrame,argumentsFor,summary} from './benchmark-small-models.mjs'

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
