import { readFile, writeFile, mkdir, access } from 'node:fs/promises';
import { resolve, join, dirname, relative } from 'node:path';
import { execFileSync } from 'node:child_process';
import { request as https, Agent } from 'node:https';
import { randomUUID, randomBytes, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Only a new fictional full deployment. No browser/SDK/network bypass; failures never retry writes.
const args=process.argv.slice(2);
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw Error('Usage: verify-operations.mjs --deployment new-full-demo --results repository-evidence');
const source=resolve(args[1]),results=resolve(args[3]);
const identity=JSON.parse(await readFile(join(source,'deployment.json'),'utf8'));
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-f0-9]{12}$/.test(identity.project)||relative(join(root,'artifacts/test-results'),results).startsWith('..'))throw Error('Owned fictional deployment required.');
const backup=source+'-backup',restored=source+'-restored';
for(const folder of [backup,restored]){try{await access(folder);throw Error('New isolated backup/restore paths required.');}catch(e){if(e.code!=='ENOENT')throw e;}}
await mkdir(results,{recursive:true});
const config=JSON.parse(await readFile(join(source,'operator.json'),'utf8'));
const originalUrl=config.url,reviewPort=new URL(config.secondaryUrl).port;
const agent=new Agent({ca:await readFile(join(source,config.caFile)),keepAlive:false}),jar=new Map();
const fixture={project:identity.project,operations:[],backup,restored},checkpoint=join(source,'operations-fixture.json'),stages=[];
await writeFile(checkpoint,JSON.stringify(fixture),{mode:0o600,flag:'wx'});
const save=()=>writeFile(checkpoint,JSON.stringify(fixture,null,2),{mode:0o600});
function command(file,args,options={}){try{return execFileSync(file,args,{cwd:root,encoding:'utf8',maxBuffer:16*1024*1024,timeout:180000,stdio:['ignore','pipe','pipe'],...options});}catch(e){throw Error('Owned verification command failed: '+file.split('/').at(-1)+' '+args[0]+' (private output suppressed)');}}
const svm=(directory,...args)=>command(join(directory,'svm.sh'),args);
async function compose(directory,...args){const d=JSON.parse(await readFile(join(directory,'deployment.json'),'utf8'));return command('docker',['compose','--env-file',join(directory,'deployment.env'),'--project-name',d.project,'-f',join(directory,'compose.yaml'),'--profile','full',...args]);}
async function call(path,{method='GET',body,bearer,headers={}}={}){
 const content=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
 return new Promise((accept,reject)=>{
  const req=https(new URL(path,config.url),{agent,method,headers:{...(bearer?{Authorization:'Bearer '+bearer}:{Cookie:[...jar].map(([k,v])=>k+'='+v).join('; ')}),...(content?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':content.length}:{}),...headers}},response=>{
   if(!bearer)for(const item of response.headers['set-cookie']??[]){const pair=item.split(';')[0],at=pair.indexOf('=');jar.set(pair.slice(0,at),pair.slice(at+1));}
   const chunks=[];let size=0;response.on('data',b=>{size+=b.length;if(size>4*1024*1024)req.destroy(Error('Bounded response exceeded'));else chunks.push(b);});response.on('error',reject);response.on('end',()=>{const bytes=Buffer.concat(chunks);let data;try{data=JSON.parse(bytes);}catch{}accept({status:response.statusCode,data,bytes});});
  });const timer=setTimeout(()=>req.destroy(Error('HTTPS budget exceeded')),25000);req.on('close',()=>clearTimeout(timer));req.on('error',reject);if(content)req.write(content);req.end();
 });
}
const checked=(r,path)=>{if(![200,201,202,204].includes(r.status))throw Error(`HTTP ${r.status} ${r.data?.code??''} at ${path.split('?')[0]}`);return r.data;};
const get=async path=>checked(await call(path),path);
async function write(path,body,method='POST',bearer){
 const key=randomUUID(),headers={'Idempotency-Key':key};if(!bearer)headers['X-CSRF-TOKEN']=(await get('/api/v1/session')).csrfToken;
 const operation={path,method,body,key,state:'pending'};fixture.operations.push(operation);await save();
 const result=checked(await call(path,{method,body,bearer,headers}),path);operation.state='done';await save();return result;
}
const manage=(path,body,method='POST')=>write('/api/v1/manage/'+path,body,method);
async function until(action,predicate){const end=Date.now()+90000;while(Date.now()<end){try{const value=await action();if(predicate(value))return value;}catch(e){if(!['ECONNREFUSED','ETIMEDOUT','ECONNRESET'].includes(e.code))throw e;}await new Promise(r=>setTimeout(r,500));}throw Error('Bounded readiness wait expired.');}
const stage=name=>{stages.push(name);console.log('Verified operations: '+name);};
const role=n=>({roleId:'00000000-0000-4000-a000-'+String(n).padStart(12,'0'),scopes:[{kind:'Site',resourceId:null}]});
let outcome='failed';
try{
 await until(()=>call('/api/v1/session'),r=>r.status===200);
 const session=await get('/api/v1/session');checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:config.initialPassword},headers:{'X-CSRF-TOKEN':session.csrfToken}}),'login');
 await write('/api/v1/session/password',{currentPassword:config.initialPassword,newPassword:config.password});
 const admin=(await get('/api/v1/session')).subjectId;
 async function permissions(software){const a=await get('/api/v1/manage/subjects/'+admin+'/authorization');await manage('subjects/'+admin+'/authorization',{assignments:[role(1),role(2)],directPermissions:software?['software.read','release.upload','release.publish','enrollment.manage','instance.read','instance.manage','deployment.create','deployment.control','audit.read'].map(operation=>({softwareId:software.id,operation})):[],expectedRevision:a.revision,reason:'虚构备份恢复验证授权'},'PUT');}
 await permissions();const software=await manage('software',{name:'虚构恢复验证程序',category:'Vision'});await permissions(software);
 const process=await manage('processes',{name:'虚构恢复验证工序'}),device=await manage('devices',{processId:process.id,name:'虚构恢复机台'});
 const hardware=await manage('devices/'+device.id+'/hardware',{name:'虚构恢复工控机',type:'IPC'});
 const binding=await manage('hardware/'+hardware.id+'/software-bindings',{softwareId:software.id,reason:'虚构恢复固定位置'});
 const permitSecret=randomBytes(32).toString('base64url'),secret=randomBytes(32).toString('base64url');
 const grant=await manage('enrollment-grants',{softwareId:software.id,deviceIds:[device.id],hardwareBindingId:binding.id,hardwareBindingRevision:binding.revision,expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:permitSecret,reason:'虚构恢复接入'});
 const registration=await write('/api/v1/enrollment/instances',{softwareId:software.id,deviceId:device.id,installationKey:randomUUID(),secretMaterial:secret},'POST',grant.id+'.'+permitSecret);
 const bearer=registration.credentialId+'.'+secret;fixture.instance=registration.instanceId;await save();
 const stream=await write('/api/v1/client/report-streams',{expectedEpoch:0},'POST',bearer);
 const bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64'),sha256=createHash('sha256').update(bytes).digest('hex');
 async function upload(){const r=await manage('software/'+software.id+'/releases',{changeLevel:'Patch',changeSummary:'虚构恢复归档',changeReason:'仅验证数据库与文件恢复',package:{fileName:'recovery-fixture.zip',sizeBytes:bytes.length,sha256}});await write(r.uploadPath,bytes,'PUT');return r;}
 const created=await upload();const ready=await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
 const report=(seq,installed)=>({streamEpoch:stream.streamEpoch,reportSeq:seq,reportedAt:new Date().toISOString(),installationState:installed?'Installed':'NotInstalled',installedReleaseId:installed?ready.id:null,installedVersion:installed?ready.version:null,installedAt:null,runningState:'Stopped',reportedIps:['192.0.2.150'],databaseState:{mode:'None',items:[]}});
 await write('/api/v1/client/status-reports',report(1,true),'POST',bearer);
 const evidence=(await get('/api/v1/manage/releases/'+ready.id+'/test-evidence')).items[0];
 const formal=await manage('releases/'+ready.id+'/publish',{testEvidenceId:evidence.id,publishReason:'虚构恢复验证',publishConclusion:'夹具证据，不代表现场验收',expectedRevision:ready.revision});
 await write('/api/v1/client/status-reports',report(2,false),'POST',bearer);
 const selection=await manage('target-selections',{softwareId:software.id,mode:'Explicit'});
 await manage('target-selections/'+selection.id+'/chunks/0',{instanceIds:[registration.instanceId]},'PUT');
 const selected=await get('/api/v1/manage/target-selections/'+selection.id);
 await manage('target-selections/'+selection.id+'/seal',{expectedRevision:selected.revision,chunkCount:1,expectedDistinctCount:1});
 const deployment=await manage('deployments',{softwareId:software.id,selectionId:selection.id,kind:'Update',targetReleaseId:formal.id,window:{notBefore:new Date(Date.now()+30000).toISOString(),latestStart:new Date(Date.now()+3600000).toISOString()},reason:'虚构恢复后须明确继续'});
 await until(()=>get('/api/v1/manage/deployments/'+deployment.id),d=>d.state==='Running');
 const downloaded=await call('/api/v1/packages/'+formal.packageId+'/content');assert.equal(downloaded.status,200);assert.equal(createHash('sha256').update(downloaded.bytes).digest('hex'),sha256);
 await until(()=>get('/api/v1/manage/audit-events?softwareId='+software.id),r=>r.items.length>0&&r.items.every(x=>x.endedAt));
 await compose(source,'stop','-t','30','worker-a','worker-b');
 const pending=await upload();fixture.pendingRelease=pending.release.id;fixture.deploymentId=deployment.id;await save();
 const diagnostics=await compose(source,'exec','-T','api-a','dotnet','/opt/svm/Svm.HttpApi/Svm.HttpApi.dll','--diagnostics');
 const diagnostic=JSON.parse(diagnostics);assert.equal(diagnostic.status,'Available');assert.ok(diagnostic.metrics.find(m=>m.name==='outbox.pending').count>0);
 await writeFile(join(results,'before-diagnostics.json'),diagnostics);stage('HTTPS facts, confirmed package, queued task and unsent outbox recorded');
 const backupOutput=svm(source,'backup',backup);await writeFile(join(results,'backup-command.txt'),backupOutput);
 svm(source,'verify-backup',backup);svm(source,'restore',backup,restored);
 const restoredIdentity=JSON.parse(await readFile(join(restored,'deployment.json'),'utf8'));assert.notEqual(restoredIdentity.project,identity.project);
 assert.equal(command('docker',['ps','-q','--filter','label=com.docker.compose.project='+restoredIdentity.project]).trim(),'');
 svm(source,'down'); // Free this fixture's original subnet; bind-mounted source data remain intact.
 stage('cold database roles, broker store, identity, files, logs and artifact restored into a new fenced directory');
 svm(restored,'build');svm(restored,'review-up',reviewPort);config.url='https://'+new URL(originalUrl).hostname+':'+reviewPort;
 await until(()=>call('/api/v1/session'),r=>r.status===200);
 const reviewApi=JSON.parse(command('docker',['inspect',restoredIdentity.project+'-api-a-1']))[0];
 assert.deepEqual(Object.keys(reviewApi.NetworkSettings.Networks),[restoredIdentity.project+'_svm']);
 assert.equal(JSON.parse(command('docker',['network','inspect',restoredIdentity.project+'_svm']))[0].Internal,true);
 // Existing personnel cookie and independent credential use the restored protection/key facts.
 assert.equal((await get('/api/v1/session')).subjectId,admin);
 assert.equal((await get('/api/v1/manage/instances/'+registration.instanceId)).id,registration.instanceId);
 assert.equal((await get('/api/v1/manage/releases/'+formal.id)).state,'Formal');
 for(const path of ['/api/v1/enrollment/instances','/api/v1/client/attempts/'+randomUUID()+'/start','/api/v1/manage/deployments']){
  const denied=await call(path,{method:'POST',body:{}});assert.equal(denied.status,503);assert.equal(denied.data.detail,'RECOVERY_REVIEW_REQUIRED');
 }
 assert.equal((await call('/api/v1/packages/'+formal.packageId+'/content')).status,503);
 await write('/api/v1/client/status-reports',report(3,false),'POST',bearer);
 assert.equal((await get('/api/v1/manage/releases/'+pending.release.id)).state,pending.release.state);
 assert.equal(command('docker',['ps','-q','--filter','label=com.docker.compose.project='+restoredIdentity.project,'--filter','label=com.docker.compose.service=worker-a']).trim(),'');
 stage('isolated review preserves sessions and reports while blocking enrollment, downloads, starts and workers');
 const d=await get('/api/v1/manage/deployments/'+deployment.id);
 await manage('deployments/'+d.id+'/pause',{expectedRevision:d.revision,reason:'核对备份后现场动作，保持暂停等待统一回归'});
 const approval=join(restored,'recovery','fixture-review.json');await writeFile(approval,JSON.stringify({operator:'fictional recovery operator',reason:'Disposable recovery exercise; original deployment stopped, post-backup actions reconciled',evidenceReference:'operations-recovery/summary.json',sourceStopped:true,postBackupChangesReconciled:true,pendingWorkReviewed:true,identityMaterialReviewed:true}),{mode:0o600});
 svm(restored,'reopen',approval);
 assert.equal(command('docker',['ps','-q','--filter','label=com.docker.compose.project='+restoredIdentity.project]).trim(),'');
 svm(restored,'up');config.url=originalUrl;
 await until(()=>call('/api/v1/session'),r=>r.status===200);
 await until(()=>get('/api/v1/manage/releases/'+pending.release.id),r=>r.state==='Test');
 assert.equal((await get('/api/v1/manage/deployments/'+deployment.id)).state,'Paused');
 const after=await call('/api/v1/packages/'+formal.packageId+'/content');assert.equal(after.status,200);assert.equal(createHash('sha256').update(after.bytes).digest('hex'),sha256);
 stage('explicit reopening resumes original package work, retains paused deployment and verified download');
 outcome='passed';
}finally{
 for(const directory of [restored,source]){try{await access(join(directory,'svm.sh'));svm(directory,'down');}catch{}}
 agent.destroy();await writeFile(join(results,'summary.json'),JSON.stringify({outcome,commit:identity.releaseCommit,dirty:identity.releaseDirty,platform:identity.platform,hostPlatform:process.platform,hostArchitecture:process.arch,stages,resources:{source,backup,restored,containers:'stopped by owned Compose down; private directories require explicit final cleanup'},scope:'One fictional full Linux deployment. No developer database migration, real GitLab, manufacturer installation, production RPO/RTO or HA acceptance.'},null,2)+'\n');
}
