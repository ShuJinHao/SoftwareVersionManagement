import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Runs only against an explicitly prepared, disposable full deployment. Never connects to a PLC.
const args=process.argv.slice(2),resume=args.at(-1)==='--resume';if(resume)args.pop();
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw Error('Usage: eng/node deploy/linux/verify-plc.mjs --deployment folder --results folder [--resume]');
const deployment=resolve(args[1]),results=resolve(args[3]);
const config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8')),identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
const rel=relative(join(root,'artifacts/test-results'),results);
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-f0-9]{12}$/.test(identity.project)||!rel||rel.startsWith('..')||new URL(config.url).protocol!=='https:')throw Error('Only an owned full demo and repository evidence directory are allowed.');
await mkdir(results,{recursive:true});
const agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true}),jar=new Map(),fixturePath=join(deployment,'plc-fixture.json');
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
async function stage(name){stages.push(name);console.log('Verified PLC: '+name);}
const role=n=>({roleId:'00000000-0000-4000-a000-'+String(n).padStart(12,'0'),scopes:[{kind:'Site',resourceId:null}]});
async function authorize(name,operations){const a=await get('/api/v1/manage/subjects/'+fixture.adminId+'/authorization');return manage(name,'subjects/'+fixture.adminId+'/authorization',{assignments:[role(1),role(2)],directPermissions:operations.map(operation=>({softwareId:fixture.software?.id,operation})),expectedRevision:a.revision,reason:'仅虚构 PLC 验证，显式产品与现场授权'},'PUT');}
async function capture(name){await page.screenshot({path:join(results,name),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]});screenshots.push(name);}
async function browserWrite(name,path,method,action){return operation(name,async()=>{
 const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()===method),received=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()===method);
 await action();const r=await sent;Object.assign(fixture.operations[name],{path,method,body:r.postDataJSON(),key:r.headers()['idempotency-key']});await save();const response=await received;if(!response.ok())throw Error('Browser HTTP '+response.status()+' at '+path);return response.json();
});}
try{
 try{fixture=JSON.parse(await readFile(fixturePath,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 if(fixture){if(!resume||fixture.project!==identity.project||fixture.commit!==identity.releaseCommit)throw Error('Existing fixture requires matching --resume.');}
 else{if(resume)throw Error('Missing checkpoint.');fixture={project:identity.project,commit:identity.releaseCommit,operations:{}};await save();}
 for(const [name,op]of Object.entries(fixture.operations))if(op.state!=='done')throw Error('Unconfirmed operation '+name+'; no automatic repeat.');
 const initial=await get('/api/v1/session');
 checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:fixture.operations.password?config.password:config.initialPassword},headers:{'X-CSRF-TOKEN':initial.csrfToken}}),'login');
 if(!fixture.operations.password)await operation('password',async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'first password'));
 fixture.adminId=(await get('/api/v1/session')).subjectId;await save();
 await authorize('setup-role',[]);
 fixture.software=await manage('software','software',{name:'虚构 PLC 主程序库',category:'PLC'});
 await authorize('plc-permissions',['software.read','release.upload','release.publish','release.disable','release.source.manage','plc.verify','plc.usage.record','plc.uploadCredential.manage','enrollment.manage','deployment.create','audit.read']);
 fixture.process=await manage('process','processes',{name:'虚构 PLC 验证工序'});
 fixture.device=await manage('device','devices',{processId:fixture.process.id,name:'虚构 PLC 验证设备'});
 fixture.hardware=[];fixture.positions=[];
 for(const name of ['A','B']){
  const h=await manage('hardware-'+name,'devices/'+fixture.device.id+'/hardware',{name:'虚构控制器 '+name,type:'PLC',managementIp:'192.0.2.'+(name==='A'?31:32)});fixture.hardware.push(h);
  const p=await manage('binding-'+name,'hardware/'+h.id+'/software-bindings',{softwareId:fixture.software.id,reason:'验证多控制器共享主程序库'});fixture.positions.push(p);
 }
 fixture.secret??=randomBytes(32).toString('base64url');await save();const position=fixture.positions[0];
 fixture.credential=await manage('credential','software/'+fixture.software.id+'/plc-upload-credentials',{name:'虚构归档工具',scope:[{bindingId:position.id,bindingRevision:position.revision}],expiresAt:new Date(Date.now()+3600000).toISOString(),secretMaterial:fixture.secret});
 const bearer=fixture.credential.id+'.'+fixture.secret;
 const list=await get('/api/v1/manage/software/'+fixture.software.id+'/plc-upload-credentials');assert.equal(list.items.length,1);assert.ok(!JSON.stringify(list).includes(fixture.secret));
 await stage('explicit PLC library, two controllers, scoped upload credential without returned secret');
 phase='API archive and double replicas';
 const archiveBody={bindingId:position.id,bindingRevision:position.revision,changeLevel:'Patch',changeSummary:'虚构 PLC 归档',changeReason:'协议验证，不执行厂商工程',package:{fileName:'plc-demo.zip',sizeBytes:bytes.length,sha256}};
 fixture.archive=await write('archive','/api/v1/plc-upload/archives',archiveBody,'POST',bearer);await save();
 await operation('upload',async()=>checked(await call(fixture.archive.uploadPath,{method:'PUT',body:bytes,bearer}),'PLC upload'));
 const ready=await until(()=>get('/api/v1/manage/releases/'+fixture.archive.release.id),r=>['Test','Formal'].includes(r.state));assert.equal(ready.sourceKind,'PLC');
 const pkg=fixture.operations.revoke?await get('/api/v1/manage/packages/'+ready.packageId):checked(await call('/api/v1/plc-upload/uploads/'+fixture.archive.uploadId,{bearer}),'owned upload');assert.equal(pkg.state,'Ready');
 if(!fixture.operations['ui-usage'])assert.equal((await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage')).current,null);
 await stage(phase);
 phase='restricted protocol';
 if(!fixture.operations.denials)await operation('denials',async()=>{
  const headers={'Idempotency-Key':randomUUID()};
  const invalid=await call('/api/v1/plc-upload/archives',{method:'POST',body:{...archiveBody,softwareId:fixture.software.id},bearer,headers});assert.equal(invalid.status,400);
  const outside=await call('/api/v1/plc-upload/archives',{method:'POST',body:{...archiveBody,bindingId:fixture.positions[1].id,bindingRevision:fixture.positions[1].revision},bearer,headers:{'Idempotency-Key':randomUUID()}});assert.equal(outside.status,403);
  const context=await call('/api/v1/client/context',{bearer});assert.equal(context.status,401);
  const csrf=(await get('/api/v1/session')).csrfToken;
  const enrollment=await call('/api/v1/manage/enrollment-grants',{method:'POST',body:{softwareId:fixture.software.id,deviceIds:[fixture.device.id],hardwareBindingId:position.id,hardwareBindingRevision:position.revision,expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:randomBytes(32).toString('base64url'),reason:'应拒绝 PLC 在线登记'},headers:{'X-CSRF-TOKEN':csrf,'Idempotency-Key':randomUUID()}});assert.equal(enrollment.status,409);
  const caps=await call('/api/v1/manage/software/'+fixture.software.id+'/deployment-capabilities');assert.equal(caps.status,409);
  return {unknownField:invalid.status,wrongController:outside.status,onlineCredential:context.status,enrollment:enrollment.status,onlineTasks:caps.status};
 });
 await stage(phase);
 phase='controller-context download';
 const beforeDownload=await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage');
 const download='/api/v1/plc-bindings/'+position.id+'/packages/'+ready.packageId+'/content';
 const head=await call(download,{method:'HEAD'});assert.equal(head.status,200);assert.equal(Number(head.headers['content-length']),bytes.length);
 const range=await call(download,{headers:{Range:'bytes=0-3'}});assert.equal(range.status,206);assert.deepEqual(range.bytes,bytes.subarray(0,4));
 const full=await call(download);assert.equal(full.status,200);assert.equal(createHash('sha256').update(full.bytes).digest('hex'),sha256);
 assert.deepEqual(await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage'),beforeDownload);
 await stage(phase+'; HEAD, Range, checksum, no inferred usage');
 process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');
 const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
 browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});
 const context=await browser.newContext({viewport:{width:1440,height:1000}});page=await context.newPage();page.setDefaultTimeout(15000);const errors=[];page.on('pageerror',e=>errors.push(e.name));
 await page.goto(config.url+'/login');await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);await page.getByRole('button',{name:'登录',exact:true}).click();await expect(page.getByRole('navigation',{name:'主导航'})).toBeVisible();
 phase='browser manual publication';
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=releases&releaseId='+ready.id+'&channel=Test&hardwareId='+fixture.hardware[0].id+'&bindingId='+position.id);
 if(!fixture.operations['ui-publish']){
  await expect(page.getByRole('heading',{name:'人员验证转正式',exact:true})).toBeVisible();
  await page.getByRole('combobox',{name:/^验证控制器/}).selectOption(position.id);await page.getByLabel('现场验证时间',{exact:true}).fill(new Date(Date.now()-3600000).toISOString().slice(0,16));
  await page.getByLabel('验证记录',{exact:true}).fill('虚构协议流程验证；不代表实际 PLC 程序通过现场验证。');await page.getByLabel('材料说明或引用',{exact:true}).fill('仅一次性测试夹具');await page.getByLabel('发布原因',{exact:true}).fill('验证人工发布流程');await page.getByLabel('发布结论',{exact:true}).fill('协议流程验证通过');
  await capture('plc-publication-form.png');
  fixture.published=await browserWrite('ui-publish','/api/v1/manage/releases/'+ready.id+'/plc-publish','POST',()=>page.getByRole('button',{name:'验证通过，转正式',exact:true}).click());await save();
 }
 const formal=await get('/api/v1/manage/releases/'+ready.id);assert.equal(formal.state,'Formal');assert.ok(formal.plcEvidenceId);assert.equal(formal.testEvidenceId,null);
 if(!fixture.operations['ui-usage'])assert.equal((await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage')).current,null);
 await expect(page.getByRole('heading',{name:'PLC 现场验证',exact:true})).toBeVisible();await capture('plc-formal.png');await stage(phase+' without Installed evidence or inferred usage');
 phase='browser append-only controller usage';
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=plcUsage&hardwareId='+fixture.hardware[0].id+'&bindingId='+position.id);
 if(!fixture.operations['ui-usage']){
  await page.getByRole('button',{name:'登记现场使用',exact:true}).click();const dialog=page.getByRole('dialog',{name:'登记现场使用',exact:true});
  await dialog.getByRole('combobox',{name:/^实际使用版本/}).selectOption(ready.id);await dialog.getByLabel('现场时间',{exact:true}).fill(new Date(Date.now()-1800000).toISOString().slice(0,16));await dialog.getByLabel('说明',{exact:true}).fill('虚构现场确认，仅验证人工记录');await dialog.getByLabel('材料说明或引用',{exact:true}).fill('一次性协议测试材料');
  await browserWrite('ui-usage','/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage','POST',()=>dialog.getByRole('button',{name:'保存记录',exact:true}).click());await expect(dialog).not.toBeVisible();
 }
 const current=await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage');assert.equal(current.current.releaseId,ready.id);assert.equal(current.revision,1);
 assert.equal((await get('/api/v1/manage/hardware-bindings/'+fixture.positions[1].id+'/plc-usage')).current,null);
 await expect(page.getByRole('region',{name:'PLC 使用历史'})).toContainText('1.0.0');await capture('plc-usage.png');
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id);await expect(page.getByRole('region',{name:'PLC 程序与人工确认版本'})).toContainText('待人工确认');await expect(page.getByRole('region',{name:'PLC 程序与人工确认版本'})).toContainText('1.0.0');await capture('plc-workspace.png');await stage(phase);
 phase='browser archive upload';
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=releases&channel=Test');
 if(!fixture.operations['ui-upload']){
  await page.getByRole('button',{name:'登记版本并上传',exact:true}).click();const form=page.locator('.upload-panel');
  await form.getByLabel('更新内容',{exact:true}).fill('虚构网页 PLC 归档');await form.getByLabel('变更原因',{exact:true}).fill('验证网页摘要和上传流程');
  await form.locator('input[type=file]').setInputFiles({name:'plc-browser-demo.zip',mimeType:'application/zip',buffer:bytes});
  await expect(form.getByRole('button',{name:'登记并上传',exact:true})).toBeEnabled();
  await operation('ui-upload',async()=>{
   const path='/api/v1/manage/software/'+fixture.software.id+'/releases';
   const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()==='POST'),created=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()==='POST');
   const received=page.waitForResponse(r=>/\/api\/v1\/manage\/uploads\/[^/]+\/content$/.test(new URL(r.url()).pathname)&&r.request().method()==='PUT');
   await form.getByRole('button',{name:'登记并上传',exact:true}).click();const req=await sent;Object.assign(fixture.operations['ui-upload'],{path,body:req.postDataJSON(),key:req.headers()['idempotency-key']});await save();
   const response=await created;if(!response.ok())throw Error('Browser archive HTTP '+response.status());const value=await response.json();fixture.operations['ui-upload'].archive=value;await save();
   const uploaded=await received;if(uploaded.status()!==202)throw Error('Browser upload HTTP '+uploaded.status());return value;
  });
 }
 const browserArchive=fixture.operations['ui-upload'].value;
 const second=await until(()=>get('/api/v1/manage/releases/'+browserArchive.release.id),r=>r.state==='Test');assert.equal(second.version,'1.0.1');assert.equal(second.sourceKind,'PLC');
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=releases&releaseId='+second.id+'&channel=Test');await expect(page.getByRole('heading',{name:'人员验证转正式',exact:true})).toBeVisible();await capture('plc-browser-upload.png');await stage(phase);
 phase='revocation and retained historical download';
 await manage('revoke','plc-upload-credentials/'+fixture.credential.id+'/revoke',{expectedRevision:fixture.credential.revision,reason:'验证完成，吊销专用工具凭据'});
 assert.equal((await call('/api/v1/plc-upload/uploads/'+fixture.archive.uploadId,{bearer})).status,401);
 assert.equal((await call(download)).status,200);assert.equal((await get('/api/v1/manage/hardware-bindings/'+position.id+'/plc-usage')).current.id,current.current.id);
 assert.deepEqual(errors,[]);await stage(phase);fixture.verified=true;await save();
 await writeFile(join(results,'summary.json'),JSON.stringify({verifiedAt:now(),commit:identity.releaseCommit,project:identity.project,platform:identity.platform,stages,screenshots,scope:'Fictional PLCs; real HTTPS/PostgreSQL/RabbitMQ/Nginx and browser. No physical PLC, manufacturer or production acceptance.'},null,2)+'\n');
 console.log('PLC archive and usage flow verified. Private credentials remain only in the owned deployment.');
}catch(e){if(page)await page.screenshot({path:join(results,'failure-page.png'),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]}).catch(()=>{});await writeFile(join(results,'failure.json'),JSON.stringify({phase,name:e.name,message:String(e.message).replace(/[A-Za-z0-9_-]{32,}/g,'[redacted]'),stages},null,2)+'\n');console.error('PLC verification stopped at '+phase+'; inspect sanitized failure.json.');process.exitCode=1;}
finally{await browser?.close();agent.destroy();}
