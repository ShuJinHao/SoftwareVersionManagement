import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Runs only against an explicitly prepared, disposable full deployment. Simulates an executable instance; never installs or rolls back manufacturer software.
const args=process.argv.slice(2),resume=args.at(-1)==='--resume';if(resume)args.pop();
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw Error('Usage: eng/node deploy/linux/verify-rollback.mjs --deployment folder --results folder [--resume]');
const deployment=resolve(args[1]),results=resolve(args[3]);
const config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8')),identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
const rel=relative(join(root,'artifacts/test-results'),results);
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-f0-9]{12}$/.test(identity.project)||!rel||rel.startsWith('..')||new URL(config.url).protocol!=='https:')throw Error('Only an owned full demo and repository evidence directory are allowed.');
await mkdir(results,{recursive:true});
const agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true}),jar=new Map(),fixturePath=join(deployment,'rollback-fixture.json');
const stages=[],screenshots=[],bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64'),sha256=createHash('sha256').update(bytes).digest('hex');
let fixture,browser,page,phase='prepare';
const now=()=>new Date().toISOString(),save=()=>writeFile(fixturePath,JSON.stringify(fixture,null,2)+'\n',{mode:0o600});
async function call(path,{method='GET',body,bearer,headers={}}={}){
 const content=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
 return await new Promise((accept,reject)=>{const req=https(new URL(path,config.url),{agent,method,headers:{...(bearer?{Authorization:'Bearer '+bearer}:{Cookie:[...jar].map(([k,v])=>k+'='+v).join('; ')}),...(content?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':content.length}:{}),...headers}},response=>{
  if(!bearer)for(const value of response.headers['set-cookie']??[]){const pair=value.split(';')[0],i=pair.indexOf('=');jar.set(pair.slice(0,i),pair.slice(i+1));}
  const chunks=[];let size=0;response.on('data',chunk=>{size+=chunk.length;if(size>4*1024*1024)req.destroy(Error('Response limit'));else chunks.push(chunk);});response.on('error',reject);response.on('end',()=>{const bytes=Buffer.concat(chunks);let data=null;try{data=JSON.parse(bytes.toString());}catch{}accept({status:response.statusCode,headers:response.headers,bytes,data});});
 });const timer=setTimeout(()=>req.destroy(Error('HTTPS budget exceeded')),25000);req.once('close',()=>clearTimeout(timer));req.on('error',reject);if(content)req.write(content);req.end();});
}
function checked(r,path){if(![200,201,202,204,206].includes(r.status))throw Error('HTTP '+r.status+' '+(r.data?.code??'')+' at '+path.split('?')[0]);return r.data;}
const get=async path=>checked(await call(path),path);
async function operation(name,action){const old=fixture.operations[name];if(old?.state==='done')return old.value;if(old)throw Error('Unconfirmed operation '+name+' requires original-operation verification.');fixture.operations[name]={state:'pending'};await save();const value=await action();fixture.operations[name]={...fixture.operations[name],state:'done',value};await save();return value;}
async function write(name,path,body,method='POST',bearer){return operation(name,async()=>{
 const key=randomUUID(),headers={'Idempotency-Key':key};if(!bearer)headers['X-CSRF-TOKEN']=(await get('/api/v1/session')).csrfToken;
 Object.assign(fixture.operations[name],{path,method,body,key});await save();return checked(await call(path,{method,body,bearer,headers}),path);
});}
const manage=(name,path,body,method='POST')=>write(name,'/api/v1/manage/'+path,body,method);
async function until(action,predicate){const end=Date.now()+90000;while(Date.now()<end){const r=await action();if(predicate(r))return r;await new Promise(r=>setTimeout(r,500));}throw Error('Bounded wait at '+phase);}
async function stage(name){stages.push(name);console.log('Verified rollback: '+name);}
const role=n=>({roleId:'00000000-0000-4000-a000-'+String(n).padStart(12,'0'),scopes:[{kind:'Site',resourceId:null}]});
async function authorize(name,operations){const a=await get('/api/v1/manage/subjects/'+fixture.adminId+'/authorization');return manage(name,'subjects/'+fixture.adminId+'/authorization',{assignments:[role(1),role(2)],directPermissions:operations.map(operation=>({softwareId:fixture.software?.id,operation})),expectedRevision:a.revision,reason:'仅虚构回退验证，显式产品与现场授权'},'PUT');}
async function capture(name){await page.screenshot({path:join(results,name),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]});screenshots.push(name);}
async function browserWrite(name,path,method,action){return operation(name,async()=>{
 const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()===method),received=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()===method);
 await action();const r=await sent;Object.assign(fixture.operations[name],{path,method,body:r.postDataJSON(),key:r.headers()['idempotency-key']});await save();const response=await received;if(!response.ok())throw Error('Browser HTTP '+response.status()+' at '+path);return response.json();
});}
function report(i,release,seq,mode='None'){return {streamEpoch:i.streamEpoch,reportSeq:seq,reportedAt:now(),installationState:'Installed',installedReleaseId:release.id,installedVersion:release.version,installedAt:null,runningState:'Stopped',reportedIps:[i.ip],databaseState:{mode,items:mode==='Present'?[{databaseKey:'recipe',schemaId:'incompatible-schema'}]:[]}};}
const client=(name,i,path,body)=>write(name,'/api/v1/client/'+path,body,'POST',i.bearer);
try{
 try{fixture=JSON.parse(await readFile(fixturePath,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 if(fixture){if(!resume||fixture.project!==identity.project||fixture.commit!==identity.releaseCommit)throw Error('Existing checkpoint requires a matching --resume.');}
 else{if(resume)throw Error('Missing checkpoint.');fixture={project:identity.project,commit:identity.releaseCommit,operations:{}};await save();}
 for(const [name,op]of Object.entries(fixture.operations))if(op.state!=='done')throw Error('Unconfirmed operation '+name+'; verify its original request before continuing.');
 const initial=await get('/api/v1/session');checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:fixture.operations.password?config.password:config.initialPassword},headers:{'X-CSRF-TOKEN':initial.csrfToken}}),'login');
 if(!fixture.operations.password)await operation('password',async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'password'));
 fixture.adminId=(await get('/api/v1/session')).subjectId;await save();await authorize('site-roles',[]);
 fixture.software=await manage('software','software',{name:'虚构回退程序',category:'UpperComputer'});
 await authorize('product-rights',['software.read','release.upload','release.publish','enrollment.manage','instance.read','instance.manage','deployment.create','deployment.control','deployment.rollback']);
 fixture.process=await manage('process','processes',{name:'虚构回退验证工序'});fixture.devices=[];fixture.positions=[];fixture.instances=[];
 for(const [n,name]of ['A','B','C'].entries()){
  const d=await manage('device-'+name,'devices',{processId:fixture.process.id,name:'虚构回退设备 '+name});fixture.devices.push(d);
  const h=await manage('hardware-'+name,'devices/'+d.id+'/hardware',{name:'虚构工控机 '+name,type:'IPC',managementIp:'192.0.2.'+(10+n)});
  const b=await manage('binding-'+name,'hardware/'+h.id+'/software-bindings',{softwareId:fixture.software.id,reason:'虚构固定安装位置'});fixture.positions.push(b);
  const permitSecret=fixture['grantSecret'+name]??=randomBytes(32).toString('base64url'),secret=fixture['secret'+name]??=randomBytes(32).toString('base64url');await save();
  const grant=await manage('grant-'+name,'enrollment-grants',{softwareId:fixture.software.id,deviceIds:[d.id],hardwareBindingId:b.id,hardwareBindingRevision:b.revision,expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:permitSecret,reason:'虚构接入'});
  const registration=await write('register-'+name,'/api/v1/enrollment/instances',{softwareId:fixture.software.id,deviceId:d.id,installationKey:fixture['installationKey'+name]??=randomUUID(),secretMaterial:secret},'POST',grant.id+'.'+permitSecret);
  const i={id:registration.instanceId,bearer:registration.credentialId+'.'+secret,ip:'192.0.2.'+(80+n)};
  const stream=await client('stream-'+name,i,'report-streams',{expectedEpoch:0});i.streamEpoch=stream.streamEpoch;fixture.instances.push(i);await save();
 }
 phase='two formal versions with real double replicas and installation evidence';
 fixture.releases=[];
 for(const n of [0,1]){
  const created=await manage('release-'+n,'software/'+fixture.software.id+'/releases',{changeLevel:'Patch',changeSummary:'虚构回退版本 '+n,changeReason:'协议夹具，不执行现场安装',package:{fileName:'rollback-'+n+'.zip',sizeBytes:bytes.length,sha256}});
  await operation('upload-'+n,async()=>checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'upload'));
  const ready=fixture.operations['publish-'+n]?.value??await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
  await client('evidence-'+n,fixture.instances[0],'status-reports',report(fixture.instances[0],ready,n+1));
  const evidence=(await get('/api/v1/manage/releases/'+ready.id+'/test-evidence')).items[0];
  const formal=await manage('publish-'+n,'releases/'+ready.id+'/publish',{testEvidenceId:evidence.id,publishReason:'虚构验证发布',publishConclusion:'模拟安装证据，仅供协议验证',expectedRevision:ready.revision});fixture.releases.push(formal);
 }
 const [target,source]=fixture.releases;assert.equal(target.version,'1.0.0');assert.equal(source.version,'1.0.1');
 for(const [n,i]of fixture.instances.entries())await client('current-'+n,i,'status-reports',report(i,source,n===0?3:1,n===1?'Present':'None'));
 await stage(phase);
 process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
 browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});page=await browser.newPage({viewport:{width:1440,height:1000}});page.setDefaultTimeout(20000);const errors=[];page.on('pageerror',e=>errors.push(e.name));
 await page.goto(config.url+'/login');await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);await page.getByRole('button',{name:'登录',exact:true}).click();await expect(page.getByRole('navigation',{name:'主导航'})).toBeVisible();
 phase='browser immutable compatibility declaration';
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=releases&channel=Formal&releaseId='+target.id);
 await page.getByText('回退兼容依据',{exact:true}).click();
 if(!fixture.operations['ui-declaration']){
  await page.getByRole('button',{name:'登记新兼容修订',exact:true}).click();await page.getByLabel('允许的来源版本',{exact:true}).selectOption(source.id);await page.getByLabel('兼容说明',{exact:true}).fill('虚构程序在明确无本地数据库时兼容');await page.getByLabel('登记原因',{exact:true}).fill('验证部分回退，不代表厂家数据保护验收');
  fixture.declaration=await browserWrite('ui-declaration','/api/v1/manage/releases/'+target.id+'/compatibility-declarations','POST',()=>page.getByRole('button',{name:'登记新修订',exact:true}).click());await save();
 }else fixture.declaration=fixture.operations['ui-declaration'].value;
 await expect(page.getByText('虚构程序在明确无本地数据库时兼容',{exact:true})).toBeVisible();await capture('rollback-compatibility.png');await stage(phase);
 phase='browser partial selection and rollback with isolated rejection';
 if(!fixture.operations['ui-rollback']){
  await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id);
  for(const name of ['A','B'])await page.locator('.installation-table tbody tr').filter({hasText:'虚构回退设备 '+name}).getByRole('checkbox').check();
  await page.getByRole('button',{name:'更新／回退所选软件',exact:true}).click();await page.getByLabel(/^操作/).selectOption('Rollback');await page.getByLabel(/^正式目标版本/).selectOption(target.id);await page.getByLabel('使用配置的下一时段',{exact:true}).uncheck();await page.getByLabel('UTC 开始',{exact:true}).fill(new Date(Date.now()-120000).toISOString().slice(0,16));await page.getByLabel('UTC 最晚开始',{exact:true}).fill(new Date(Date.now()+3600000).toISOString().slice(0,16));await page.getByLabel('投放原因',{exact:true}).fill('仅选择 A、B；B 应因数据库不兼容拒绝');
  fixture.rollback=await browserWrite('ui-rollback','/api/v1/manage/deployments','POST',()=>page.getByRole('button',{name:'封存目标并创建回退',exact:true}).click());await save();
 }else fixture.rollback=fixture.operations['ui-rollback'].value;
 assert.equal(fixture.rollback.kind,'Rollback');const id=fixture.rollback.id;
 const tasks=(await until(()=>get('/api/v1/manage/deployments/'+id+'/tasks'),r=>r.items.length===1&&['Available','Claimed','AwaitingResult','Succeeded'].includes(r.items[0].state))).items;const task=tasks[0];
 assert.equal(task.instanceId,fixture.instances[0].id);assert.equal(task.kind,'Rollback');assert.equal(task.admissionCompatibility.declarationId,fixture.declaration.id);
 const admission=(await get('/api/v1/manage/deployments/'+id+'/targets')).items;assert.equal(admission.length,2);assert.equal(admission.find(x=>x.instanceId===fixture.instances[1].id).reasonCode,'DATABASE_INCOMPATIBLE');
 await page.getByRole('button',{name:'刷新当前投放',exact:true}).click();await expect(page.getByText('现场数据库未知或与声明不符',{exact:false})).toBeVisible();await capture('rollback-partial-admission.png');await stage(phase);
 phase='download, current compatibility permit, receipt and independent report';
 const downloaded=await call('/api/v1/packages/'+target.packageId+'/content');assert.equal(downloaded.status,200);assert.equal(createHash('sha256').update(downloaded.bytes).digest('hex'),sha256);
 const i=fixture.instances[0],claim=await client('claim',i,'tasks/'+task.id+'/claim',{});
 const permit=await client('start',i,'attempts/'+claim.attemptId+'/start',{stateReport:report(i,source,4),preflight:{downloadedSha256:sha256,dataProtectionConfirmed:true,compatibilityDeclarationRevision:fixture.declaration.declarationRevision}});
 assert.equal(permit.kind,'Rollback');assert.equal(permit.compatibility.declarationId,fixture.declaration.id);
 await client('receipt',i,'attempts/'+claim.attemptId+'/receipts',{eventId:randomUUID(),sequence:1,kind:'Terminal',result:'Succeeded'});
 if(!fixture.operations['installed'])assert.equal((await get('/api/v1/manage/instances/'+i.id)).lastSnapshot.installedReleaseId,source.id);
 await client('installed',i,'status-reports',report(i,target,5));assert.equal((await get('/api/v1/manage/instances/'+i.id)).lastSnapshot.installedReleaseId,target.id);
 for(const other of fixture.instances.slice(1))assert.equal((await get('/api/v1/manage/instances/'+other.id)).lastSnapshot.installedReleaseId,source.id);
 assert.equal((await get('/api/v1/manage/instances/'+fixture.instances[2].id)).latestTaskId,null);await stage(phase);
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id);await expect(page.locator('.installation-table tbody tr').filter({hasText:'虚构回退设备 A'})).toContainText('1.0.0');await expect(page.locator('.installation-table tbody tr').filter({hasText:'虚构回退设备 C'})).toContainText('1.0.1');await capture('rollback-installed-facts.png');assert.deepEqual(errors,[]);
 fixture.verified=true;await save();await writeFile(join(results,'summary.json'),JSON.stringify({verifiedAt:now(),commit:identity.releaseCommit,project:identity.project,platform:identity.platform,stages,screenshots,scope:'Real Linux HTTPS/API/PostgreSQL/RabbitMQ/Nginx/browser; fictional instances and reports. No manufacturer installation, database protection or production acceptance.'},null,2)+'\n');
 console.log('Partial rollback flow verified; private credentials remain in the owned demo only.');
}catch(e){if(page)await page.screenshot({path:join(results,'failure-page.png'),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]}).catch(()=>{});await writeFile(join(results,'failure.json'),JSON.stringify({phase,name:e.name,message:String(e.message).replace(/[A-Za-z0-9_-]{32,}/g,'[redacted]'),stages},null,2)+'\n');console.error('Rollback verification stopped at '+phase+'; inspect sanitized failure.json.');process.exitCode=1;}
finally{await browser?.close();agent.destroy();}
