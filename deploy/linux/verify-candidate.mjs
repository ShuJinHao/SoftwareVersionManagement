import { mkdtemp, mkdir, cp, readFile, writeFile, rm, chmod, access } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { request, Agent } from 'node:https';
import assert from 'node:assert/strict';

// Build-machine test driver only. Installation uses the shipped Bash + Docker
// entry in an independent directory; no source tree is mounted into a container.
const args=process.argv.slice(2);
if(args.length!==4||args[0]!=='--bundle'||args[2]!=='--results')throw Error('Usage: verify-candidate.mjs --bundle public-candidate --results safe-evidence');
if(process.platform!=='linux'||process.arch!=='x64')throw Error('Native Linux x64 verification required.');
const original=resolve(args[1]),results=resolve(args[3]),work=await mkdtemp(join(tmpdir(),'svm-candidate-install-'));
const bundle=join(work,'public bundle'),site=join(work,'私有验收 data'),settingsFile=join(work,'settings.json'),guard=join(work,'no-sdk');
const manifest=JSON.parse(await readFile(join(original,'candidate.json'),'utf8'));
await mkdir(results,{recursive:true});await cp(original,bundle,{recursive:true});await mkdir(guard);
for(const tool of ['dotnet','node','npm','npx','openssl','git']){await writeFile(join(guard,tool),'#!/bin/sh\necho "Unexpected build-tool invocation" >&2\nexit 88\n');await chmod(join(guard,tool),0o755);}
const env={...process.env,PATH:guard+':'+process.env.PATH},stages=[];let outcome='failed',project,agent;
function command(file,parameters,extra={}) {
  try{return execFileSync(file,parameters,{cwd:work,env,encoding:'utf8',maxBuffer:16*1024*1024,timeout:300000,stdio:['ignore','pipe','pipe'],...extra});}
  catch{throw Error('Candidate verification command failed: '+file.split('/').at(-1)+' '+parameters[0]+'; private output suppressed.');}
}
const install=(...args)=>command('bash',[join(bundle,'install.sh'),...args]);
const svm=(...args)=>command(join(site,'svm.sh'),['--offline',...args]);
const deny=action=>assert.throws(action);
const stage=name=>{stages.push(name);console.log('Verified candidate: '+name);};
let descriptor;
try {
  install('verify');
  // Reject an incompatible daemon before any image load or directory mutation.
  const mock=join(guard,'docker'),trace=join(work,'unexpected-docker-action');
  await writeFile(mock,'#!/bin/sh\nif [ "$1" = info ]; then echo linux/aarch64; exit 0; fi\nprintf unexpected > "$SVM_CANDIDATE_COMMAND_TRACE"\nexit 88\n');await chmod(mock,0o755);
  env.SVM_CANDIDATE_COMMAND_TRACE=trace;deny(()=>install('load'));await assert.rejects(access(trace));await rm(mock);delete env.SVM_CANDIDATE_COMMAND_TRACE;
  // The bundle's own new tags are removed, then recreated only from its archive.
  for(const image of manifest.images)command('docker',['image','rm',image.tag]);
  install('load');
  const settings=JSON.parse(await readFile(join(bundle,'acceptance-settings.template.json'),'utf8'));
  Object.assign(settings,{siteName:'虚构完整候选版安装验收',publicHost:'localhost',publicBind:'127.0.0.1',httpsPort:19443,secondaryHttpsPort:19444,
    dataDirectory:site,serviceUid:process.getuid(),serviceGid:process.getgid(),employeeNo:'RC-FIXTURE-ADMIN',administratorName:'虚构候选版管理员'});
  if(settings.serviceUid===0)Object.assign(settings,{serviceUid:10001,serviceGid:10001});
  await writeFile(settingsFile,JSON.stringify(settings),{mode:0o600});
  install('check','--settings',settingsFile);await assert.rejects(access(site));
  const invalid=join(work,'invalid.json');await writeFile(invalid,JSON.stringify({...settings,certificateMode:'production'}),{mode:0o600});deny(()=>install('check','--settings',invalid));
  const recipe=join(bundle,'templates/Dockerfile'),originalRecipe=await readFile(recipe);await writeFile(recipe,'tampered');deny(()=>install('verify'));await writeFile(recipe,originalRecipe);
  stage('public hashes, offline image loading, explicit configuration and invalid input rejection');
  install('init','--settings',settingsFile);
  descriptor=JSON.parse(await readFile(join(site,'deployment.json'),'utf8'));project=descriptor.project;
  assert.equal(descriptor.purpose,'acceptance');assert.equal(descriptor.demo,false);assert.equal(descriptor.releaseCommit,manifest.commit);
  const siteBefore=await readFile(join(site,'runtime/node-a/site.json')),operatorBefore=await readFile(join(site,'operator.json'));
  deny(()=>install('init','--settings',settingsFile));assert.deepEqual(await readFile(join(site,'operator.json')),operatorBefore);
  assert.equal(command('docker',['ps','-q','--filter','label=com.docker.compose.project='+project]).trim(),'');
  install('build','--directory',site);deny(()=>install('build','--directory',site));
  stage('new stable private identity, independent certificates, no service startup, immutable runtime assembly');
  svm('dependencies');const before=JSON.parse(svm('status'));assert.equal(before.Applied.length,0);assert.equal(before.Pending.length,17);
  const sql=svm('script');assert.ok(sql.includes('PackageCleanup'));await writeFile(join(results,'migration-review.sha256'),createHash('sha256').update(sql).digest('hex')+'\n');
  // Explicitly authorized new disposable database only; not a runtime startup migration.
  const applied=JSON.parse(svm('apply'));assert.equal(applied.Applied.length,17);assert.equal(applied.Pending.length,0);
  svm('seed');svm('validate');svm('preflight');svm('up');
  const operator=JSON.parse(operatorBefore),jar=new Map();agent=new Agent({ca:await readFile(join(site,operator.caFile))});
  function call(path,{method='GET',body,headers={},secondary=false}={}) {
    const bytes=body===undefined?null:Buffer.from(JSON.stringify(body));
    return new Promise((resolve,reject)=>{
      const req=request(new URL(path,secondary?operator.secondaryUrl:operator.url),{agent,method,headers:{Cookie:[...jar].map(([k,v])=>k+'='+v).join('; '),...(bytes?{'Content-Type':'application/json','Content-Length':bytes.length}:{}),...headers}},response=>{
        for(const raw of response.headers['set-cookie']??[]){const value=raw.split(';')[0],at=value.indexOf('=');jar.set(value.slice(0,at),value.slice(at+1));}
        const parts=[];let length=0;response.on('data',b=>{length+=b.length;if(length>1024*1024)req.destroy(Error('Response limit'));else parts.push(b);});response.on('error',reject);
        response.on('end',()=>{const text=Buffer.concat(parts).toString();let data;try{data=JSON.parse(text);}catch{}resolve({status:response.statusCode,data,text});});
      });const timeout=setTimeout(()=>req.destroy(Error('HTTPS timeout')),15000);req.on('close',()=>clearTimeout(timeout));req.on('error',reject);if(bytes)req.write(bytes);req.end();
    });
  }
  async function ready() {for(let i=0;i<60;i++){try{const r=await call('/api/v1/session');if(r.status===200)return r.data;}catch(e){if(!['ECONNREFUSED','ECONNRESET'].includes(e.code))throw e;}await new Promise(r=>setTimeout(r,500));}throw Error('Readiness timeout.');}
  const initial=await ready();assert.equal((await call('/')).status,200);assert.match((await call('/')).text,/\/assets\//);assert.equal((await call('/api/v1/unknown-acceptance-path')).status,404);
  assert.equal((await call('/api/v1/session',{method:'POST',body:{employeeNo:operator.employeeNo,password:operator.initialPassword},headers:{'X-CSRF-TOKEN':initial.csrfToken}})).status,200);
  let session=(await call('/api/v1/session')).data;assert.equal(session.mustChangePassword,true);
  const newPassword=randomBytes(32).toString('base64url')+'!9aA';
  assert.equal((await call('/api/v1/session/password',{method:'POST',body:{currentPassword:operator.initialPassword,newPassword},headers:{'X-CSRF-TOKEN':session.csrfToken,'Idempotency-Key':randomUUID()}})).status,200);
  session=(await call('/api/v1/session')).data;assert.equal(session.mustChangePassword,false);
  const users=await call('/api/v1/manage/users');assert.equal(users.status,200);assert.equal(users.data.items.length,1);
  const compose=['compose','--env-file',join(site,'deployment.offline.env'),'--project-name',project,'-f',join(site,'compose.yaml'),'--profile','full'];
  const operations=JSON.parse(command('docker',[...compose,'exec','-T','api-a','dotnet','/opt/svm/Svm.HttpApi/Svm.HttpApi.dll','--diagnostics']));assert.equal(operations.status,'Available');
  await writeFile(join(results,'operations.json'),JSON.stringify(operations,null,2));
  stage('empty database explicitly migrated and seeded, complete webpage, HTTPS login and private diagnostics');
  svm('stop');svm('up');await ready();
  assert.equal((await call('/api/v1/session')).data.subjectId,session.subjectId);
  assert.equal((await call('/api/v1/session',{secondary:true})).data.subjectId,session.subjectId);
  assert.deepEqual(await readFile(join(site,'runtime/node-a/site.json')),siteBefore);
  assert.deepEqual(await readFile(join(site,'operator.json')),operatorBefore);
  const after=JSON.parse(svm('status'));assert.equal(after.Applied.length,17);assert.equal(after.Pending.length,0);
  assert.equal((await call('/api/v1/manage/users')).data.items.length,1);
  stage('restart preserves identities, changed password, single administrator, schema and shared session');outcome='passed';
} finally {
  agent?.destroy();
  let cleaned=true;
  if(project)try{svm('down');const remaining=command('docker',['ps','-aq','--filter','label=com.docker.compose.project='+project]).trim();if(remaining)cleaned=false;}catch{cleaned=false;}
  if(project&&cleaned) {
    const tags=['svm/runtime:'+project,...['runtime','postgres','rabbitmq','nginx'].map(x=>'svm-offline/'+project+':svm_'+x+'_image')];
    for(const tag of tags)try{command('docker',['image','rm',tag]);}catch{}
  }
  if(cleaned)await rm(work,{recursive:true,force:true});
  await writeFile(join(results,'summary.json'),JSON.stringify({outcome,candidateId:manifest.candidateId,commit:manifest.commit,platform:'linux/amd64',nativeHost:process.platform+'/'+process.arch,stages,
    resources:{project,cleaned,privateDirectoryRetained:!cleaned},scope:'Independent offline installation using Bash and Docker; target commands guarded against SDK, Node, npm, OpenSSL and Git. No real GitLab, manufacturer installation, production capacity or HA acceptance.'},null,2)+'\n');
  if(!cleaned)throw Error('Owned candidate resources require cleanup; no unrelated resources were removed.');
}
