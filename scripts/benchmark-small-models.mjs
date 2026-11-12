#!/usr/bin/env node
// Milestone A: end-to-end model comparison without changing LessonForge sources.
import {spawn,spawnSync} from 'node:child_process'
import net from 'node:net'
import {readFile,writeFile,mkdir} from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {performance} from 'node:perf_hooks'

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..')
const cases=[
 {id:'science',topic:'Photosynthesis',audience_age:10},
 {id:'history',topic:'The causes of World War I',audience_age:15},
 {id:'named-subject',topic:'Carmen Carrozza',audience_age:14},
].map(x=>({...x,audience_notes:'',explore_perspectives:false,quiz_config:{question_count:10,option_count:4,difficulty:'auto'}}))

function argumentsFor(argv) {
 const o={models:['qwen3:4b'],suite:'smoke',port:5189,minutes:45,
  output:path.join(os.tmpdir(),'lessonforge-small-model-'+new Date().toISOString().replace(/[:.]/g,'-'))}
 for(let i=0;i<argv.length;i++){
  const name=argv[i]; if(name==='--help') return {help:true}
  if(!['--models','--suite','--port','--timeout-minutes','--output'].includes(name)||!argv[i+1]) throw Error('Unknown or incomplete option: '+name)
  const v=argv[++i]
  if(name==='--models')o.models=v.split(',').filter(Boolean)
  if(name==='--suite')o.suite=v
  if(name==='--port')o.port=Number(v)
  if(name==='--timeout-minutes')o.minutes=Number(v)
  if(name==='--output')o.output=path.resolve(v)
 }
 if(!o.models.length||o.models.some(m=>!/^[\w./-]+:[\w./-]+$/.test(m)))throw Error('Use exact Ollama model:tag names.')
 if(!['smoke','full'].includes(o.suite))throw Error('Choose --suite smoke or full.')
 if(!Number.isInteger(o.port)||o.port<1025||o.port>65535)throw Error('Invalid API port.')
 if(!Number.isFinite(o.minutes)||o.minutes<1||o.minutes>180)throw Error('Invalid per-case timeout.')
 if(o.output===root)throw Error('Output must not overwrite the repository.')
 return o
}

function assess(saved,request){
 const issues=[],r=saved?.result
 if(!r)return {pass:false,issues:['No persisted result.']}
 if(!r.plan?.sections?.length||!r.plan?.learning_objectives?.length)issues.push('Missing lesson content.')
 const sources=r.source_records||[],known=new Set(sources.map(s=>String(s.url).toLowerCase()))
 if(!sources.length)issues.push('Missing source provenance.')
 for(const [i,s] of sources.entries())if(!s.provider_id||!s.document_sha256||!s.license_id||!s.policy_url)issues.push('Incomplete provenance '+i)
 function citations(list,where){
  if(!Array.isArray(list)||!list.length){issues.push('Missing citations: '+where);return}
  if(list.some(c=>!known.has(String(c.url).toLowerCase())))issues.push('Unrecognized citation: '+where)
 }
 citations(r.plan?.citations,'plan')
 if(r.quiz?.questions?.length!==request.quiz_config.question_count)issues.push('Incorrect question count.')
 for(const [i,q]of (r.quiz?.questions||[]).entries()){
  if(!q.stem||!q.explanation||q.options?.length!==request.quiz_config.option_count||q.options.some(v=>!String(v).trim()))issues.push('Incomplete Q'+(i+1))
  if(!Number.isInteger(q.correct_index)||q.correct_index<0||q.correct_index>=q.options?.length)issues.push('Invalid answer Q'+(i+1))
  citations(q.citations,'Q'+(i+1))
 }
 if(!Array.isArray(r.flags)||!r.notes?.trim()||!Number.isFinite(r.citation_coverage)||r.citation_coverage<0||r.citation_coverage>1)issues.push('Missing or invalid accuracy review.')
 return {pass:issues.length===0,issues,sources:sources.length,flags:r.flags?.length??null,estimatedCoverage:r.citation_coverage??null}
}

function parseFrame(frame){
 let event='message',lines=[]
 for(const line of frame.split(/\r?\n/)){
  if(line.startsWith('event:'))event=line.slice(6).trim()
  if(line.startsWith('data:'))lines.push(line.slice(5).trim())
 }
 return lines.length?{event,data:JSON.parse(lines.join('\n'))}:null
}
// Only host-owned progress codes are collected. This counts review and
// replacement activity; it does not judge whether an AI verdict was correct.
function uniquenessMetrics(events){
 const steps=events.filter(e=>e.event==='progress'&&e.data?.stage==='AnswerUniqueness')
 if(!steps.length)return null // older runs did not provide verifier telemetry
 const statuses=steps.map(e=>e.data.status)
 return {
  verified:statuses.filter(s=>s==='accepted'||s==='repaired').length,
  repairAttempts:statuses.filter(s=>s==='repair_requested').length,
  repairsAccepted:statuses.filter(s=>s==='repaired').length,
  unresolved:statuses.filter(s=>s==='unresolved').length
 }
}
function summary(r){
 const lines=['# Small-model evaluation','','Result: STRUCTURAL_PASS is not evidence of factual accuracy.',
  'Interactive educator approval gates and optional perspectives are not covered.',
  'Evaluation-only question/option/evidence traces are in answer-uniqueness-diagnostics.json; review locally before sharing.','',
  '| Model | Case | Status | Seconds | Verified Q | Repair attempts | Repairs accepted | Sources | Flags | AI-estimated coverage |',
  '|---|---|---|---:|---:|---:|---:|---:|---:|---:|']
 for(const x of r.runs){
  const a=x.assessment||{}
  const u=x.uniqueness||{}
  lines.push('| '+[x.model,x.caseId,x.status,x.seconds,u.verified??'—',
   u.repairAttempts??'—',u.repairsAccepted??'—',a.sources??'—',a.flags??'—',
   a.estimatedCoverage==null?'—':Math.round(a.estimatedCoverage*100)+'%'].join(' | ')+' |')
 }
 return lines.join('\n')+'\n'
}
// Each model and case has an independent, durable per-run JSONL file.
 // The separate consolidated artifact makes failed runs easy to inspect
 // without copying complete educational content into the benchmark report.
async function collectTrace(outputDir,diagnosticsDir,model,caseId,events){
 const runId=events.find(e=>e.event==='run')?.data?.runId??null
 const file=runId?path.join(diagnosticsDir,runId+'.answer-uniqueness.jsonl'):null
 let records=[]
 if(file){
  try{
   const raw=await readFile(file,'utf8')
   records=raw.split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line))
  }catch(e){if(e.code!=='ENOENT')throw e}
 }
 return {model,caseId,runId,diagnosticFile:records.length?path.relative(outputDir,file):null,records}
}
async function requireFreePort(port){
 await new Promise((resolve,reject)=>{
  const s=net.createServer();s.once('error',reject)
  s.listen(port,'127.0.0.1',()=>s.close(resolve))
 }).catch(e=>{throw Error('Port '+port+' is unavailable: '+e.message)})
}
async function ready(base,child){
 const until=Date.now()+60000
 while(Date.now()<until){
  if(child.exitCode!==null)throw Error('API exited with '+child.exitCode)
  try{const r=await fetch(base+'/',{signal:AbortSignal.timeout(1500)});if(r.ok)return}catch{}
  await new Promise(r=>setTimeout(r,450))
 }
 throw Error('API startup timed out.')
}
async function command(cmd,args,cwd){
 const p=spawn(cmd,args,{cwd,stdio:'inherit'})
 const code=await new Promise((resolve,reject)=>{p.on('error',reject);p.on('exit',resolve)})
 if(code!==0)throw Error(cmd+' exited '+code)
}
async function evaluate(base,request,minutes){
 const start=performance.now(),events=[],controller=new AbortController()
 const timer=setTimeout(()=>controller.abort(),minutes*60000)
 let id=null,error=null
 try{
  const r=await fetch(base+'/api/lessons/generate?interactive=false',{
   method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(request),signal:controller.signal})
  if(!r.ok||!r.body)throw Error('Generate returned HTTP '+r.status)
  let buffer='';const decoder=new TextDecoder()
  for await(const chunk of r.body){
   buffer+=decoder.decode(chunk,{stream:true})
   let m
   while((m=/\r?\n\r?\n/.exec(buffer))){
    const evt=parseFrame(buffer.slice(0,m.index));buffer=buffer.slice(m.index+m[0].length)
    if(!evt)continue
    if(evt.event==='complete')id=evt.data.id
    if(['error','aborted'].includes(evt.event))error=evt.event+': '+evt.data.message
    if(['run','stage','error','aborted','gate'].includes(evt.event)||
       (evt.event==='progress'&&evt.data?.stage==='AnswerUniqueness'))events.push({
     seconds:Math.round((performance.now()-start)/1000),event:evt.event,data:evt.data})
   }
  }
 }catch(e){error=controller.signal.aborted?'Timed out after '+minutes+' minutes.':String(e)}
 finally{clearTimeout(timer)}
 let stored=null
 if(Number.isInteger(id)){
  try{
   const response=await fetch(base+'/api/lessons/'+id,{signal:AbortSignal.timeout(10000)})
   if(!response.ok)throw Error('Saved lesson returned HTTP '+response.status)
   stored=await response.json()
  }catch(e){error='Cannot load saved lesson: '+String(e)}
 }
 const assessment=stored?assess(stored,request):null
 return {status:error?'ERROR':assessment?.pass?'STRUCTURAL_PASS':'STRUCTURAL_FAIL',
  seconds:Math.round((performance.now()-start)/1000),
  error:error??(assessment?null:'No persisted complete result.'),events,
  uniqueness:uniquenessMetrics(events),lessonId:id,assessment,storedLesson:stored}
}
async function main(){
 const o=argumentsFor(process.argv.slice(2))
 if(o.help){
  console.log('Usage: node scripts/benchmark-small-models.mjs --models qwen3:4b --suite smoke|full')
  console.log('Options: --models comma-separated tags, --output directory, --port 5189, --timeout-minutes 45')
  return
 }
 const resp=await fetch('http://127.0.0.1:11434/api/tags',{signal:AbortSignal.timeout(10000)})
  .catch(()=>{throw Error('Ollama is not listening on 127.0.0.1:11434.')})
 if(!resp.ok)throw Error('Ollama inventory HTTP '+resp.status)
 const installed=(await resp.json()).models||[]
 for(const model of [...o.models,'nomic-embed-text:v1.5'])
  if(!installed.some(m=>m.name===model))throw Error('Missing model; run: ollama pull '+model)
 await mkdir(o.output,{recursive:true})
 const published=path.join(o.output,'api-publish'),logsDir=path.join(o.output,'logs')
 const diagnosticsDir=path.join(o.output,'diagnostics')
 await mkdir(logsDir,{recursive:true})
 await mkdir(diagnosticsDir,{recursive:true})
 const diagnosticReport={schemaVersion:1,notice:'Opt-in local educational evaluation data; may contain user input and source excerpts. Review before sharing.',runs:[]}
 console.log('Publishing isolated API to '+published)
 await command('dotnet',['publish',path.join(root,'LessonForge.Api','LessonForge.Api.csproj'),
  '-c','Release','-o',published],root)
 const configPath=path.join(published,'appsettings.json')
 const baseConfig=JSON.parse(await readFile(configPath,'utf8'))
 const version=spawnSync('ollama',['--version'],{encoding:'utf8',timeout:5000})
 const report={schemaVersion:1,startedAt:new Date().toISOString(),suite:o.suite,
  host:{platform:os.platform(),ramGB:Math.round(os.totalmem()/1024**3*10)/10,
   cpu:os.cpus()[0]?.model,node:process.version,ollamaVersion:(version.stdout||version.stderr||'').trim()},
  models:o.models.map(name=>{const m=installed.find(x=>x.name===name);return {name,digest:m.digest,size:m.size}}),
  embeddingDigest:installed.find(x=>x.name==='nomic-embed-text:v1.5')?.digest,runs:[]}
 const base='http://127.0.0.1:'+o.port
 try{
  for(const [index,model]of o.models.entries()){
   await requireFreePort(o.port)
   const cfg=structuredClone(baseConfig)
   cfg.LessonForge.Llm.ChatModel=model
   cfg.LessonForge.DbPath=path.join(o.output,'lesson-'+index+'.db')
   await writeFile(configPath,JSON.stringify(cfg,null,2))
   const logs=[]
   const p=spawn('dotnet',[path.join(published,'LessonForge.Api.dll'),'--urls',base],
    {cwd:published,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',
     LESSONFORGE_EVAL_DIAGNOSTICS_DIR:diagnosticsDir},stdio:['ignore','pipe','pipe']})
   for(const stream of [p.stdout,p.stderr])stream.on('data',b=>{logs.push(String(b));if(logs.length>3000)logs.shift()})
   try{
    await ready(base,p)
    for(const c of (o.suite==='full'?cases:cases.slice(0,1))){
     console.log('Model '+model+', topic '+c.topic)
     const run=await evaluate(base,c,o.minutes)
     const trace=await collectTrace(o.output,diagnosticsDir,model,c.id,run.events)
     diagnosticReport.runs.push(trace)
     report.runs.push({model,caseId:c.id,request:c,...run,
      diagnosticFile:trace.diagnosticFile,diagnosticRecords:trace.records.length})
     // Persist after EACH case, including failed runs; the raw JSONL records
     // already survive independently even if the API or benchmark stops.
     await writeFile(path.join(o.output,'answer-uniqueness-diagnostics.json'),
      JSON.stringify(diagnosticReport,null,2))
     await writeFile(path.join(o.output,'report.json'),JSON.stringify(report,null,2))
     await writeFile(path.join(o.output,'summary.md'),summary(report))
     console.log('  '+run.status+' ('+run.seconds+'s) '+(run.error||''))
    }
   }finally{
    p.kill()
    await new Promise(resolve=>{
     if(p.exitCode!==null)return resolve()
     const t=setTimeout(resolve,5000);p.once('exit',()=>{clearTimeout(t);resolve()})
    })
    if(p.exitCode===null)p.kill('SIGKILL')
    await writeFile(path.join(logsDir,index+'-'+model.replace(/[^a-z0-9]/gi,'_')+'.log'),logs.join(''))
   }
  }
 }finally{
  await writeFile(configPath,JSON.stringify(baseConfig,null,2))
  report.finishedAt=new Date().toISOString()
  await writeFile(path.join(o.output,'report.json'),JSON.stringify(report,null,2))
  await writeFile(path.join(o.output,'summary.md'),summary(report))
 }
 console.log('Reports: '+path.join(o.output,'summary.md')+
  ', report.json and answer-uniqueness-diagnostics.json')
 if(report.runs.some(r=>r.status!=='STRUCTURAL_PASS'))process.exitCode=2
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))
 main().catch(e=>{console.error(e);process.exitCode=1})
export {assess,parseFrame,argumentsFor,summary,uniquenessMetrics,collectTrace}
