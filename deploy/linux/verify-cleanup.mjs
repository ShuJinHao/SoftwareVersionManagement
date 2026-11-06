import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Runs only against an explicitly prepared, disposable full deployment. Uses fictional package records; never deletes production data or manufacturer software.
const args=process.argv.slice(2),resume=args.at(-1)==='--resume';if(resume)args.pop();
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw Error('Usage: eng/node deploy/linux/verify-cleanup.mjs --deployment folder --results folder [--resume]');
const deployment=resolve(args[1]),results=resolve(args[3]);
const config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8')),identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
const rel=relative(join(root,'artifacts/test-results'),results);
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-f0-9]{12}$/.test(identity.project)||!rel||rel.startsWith('..')||new URL(config.url).protocol!=='https:')throw Error('Only an owned full demo and repository evidence directory are allowed.');
await mkdir(results,{recursive:true});
const agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true}),jar=new Map(),fixturePath=join(deployment,'cleanup-fixture.json');
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
async function stage(name){stages.push(name);console.log('Verified cleanup: '+name);}
const role=n=>({roleId:'00000000-0000-4000-a000-'+String(n).padStart(12,'0'),scopes:[{kind:'Site',resourceId:null}]});
async function authorize(name,operations){const a=await get('/api/v1/manage/subjects/'+fixture.adminId+'/authorization');return manage(name,'subjects/'+fixture.adminId+'/authorization',{assignments:[role(1),role(2)],directPermissions:operations.map(operation=>({softwareId:fixture.software?.id,operation})),expectedRevision:a.revision,reason:'仅虚构包清理验证，显式产品与现场授权'},'PUT');}
async function capture(name){await page.screenshot({path:join(results,name),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]});screenshots.push(name);}
async function browserWrite(name,path,method,action){return operation(name,async()=>{
 const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()===method),received=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()===method);
 await action();const r=await sent;Object.assign(fixture.operations[name],{path,method,body:r.postDataJSON(),key:r.headers()['idempotency-key']});await save();const response=await received;if(!response.ok())throw Error('Browser HTTP '+response.status()+' at '+path);return response.json();
});}
try{
 try{fixture=JSON.parse(await readFile(fixturePath,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 if(fixture){if(!resume||fixture.project!==identity.project||fixture.commit!==identity.releaseCommit)throw Error('Existing checkpoint requires a matching --resume.');}
 else{if(resume)throw Error('Missing checkpoint.');fixture={project:identity.project,commit:identity.releaseCommit,operations:{}};await save();}
 for(const [name,op]of Object.entries(fixture.operations))if(op.state!=='done')throw Error('Unconfirmed operation '+name+'; verify its original request before continuing.');
 const initial=await get('/api/v1/session');checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:fixture.operations.password?config.password:config.initialPassword},headers:{'X-CSRF-TOKEN':initial.csrfToken}}),'login');
 if(!fixture.operations.password)await operation('password',async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'password'));
 fixture.adminId=(await get('/api/v1/session')).subjectId;await save();await authorize('site-roles',[]);
 fixture.software=await manage('software','software',{name:'虚构包清理验证程序',category:'Vision'});
 await authorize('product-rights',['software.read','release.upload','release.disable','package.clean','audit.read']);
 phase='real upload and two replicas ready';
 const created=await manage('release','software/'+fixture.software.id+'/releases',{changeLevel:'Patch',changeSummary:'仅供包清理流程验证',changeReason:'虚构归档，不执行现场安装',package:{fileName:'cleanup-fixture.zip',sizeBytes:bytes.length,sha256}});
 await operation('upload',async()=>checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'upload'));
 const ready=fixture.operations.disable?.value??await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
 if(!fixture.operations.disable){
  const downloaded=await call('/api/v1/packages/'+ready.packageId+'/content');assert.equal(downloaded.status,200);assert.equal(createHash('sha256').update(downloaded.bytes).digest('hex'),sha256);
  await until(()=>get('/api/v1/manage/audit-events?softwareId='+fixture.software.id),r=>r.items.length>0&&r.items.every(x=>x.endedAt));
 }
 await stage(phase);
 await manage('disable','releases/'+ready.id+'/disable',{reason:'虚构版本停止供包并清理',expectedRevision:ready.revision});
 phase='browser checks eligibility and fixes the cleanup target';
 process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
 browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});page=await browser.newPage({viewport:{width:1440,height:1000}});page.setDefaultTimeout(20000);const errors=[];page.on('pageerror',e=>errors.push(e.name));
 await page.goto(config.url+'/login');await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);await page.getByRole('button',{name:'登录',exact:true}).click();await expect(page.getByRole('navigation',{name:'主导航'})).toBeVisible();
 await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id+'&panel=releases&channel=Test&releaseId='+ready.id);
 await page.getByText('清理停用版本的文件',{exact:true}).click();
 if(!fixture.operations['ui-cleanup']){
  await page.getByRole('button',{name:'清理包文件',exact:true}).click();const dialog=page.getByRole('dialog');await expect(dialog).toContainText('1.0.0');
  await dialog.getByLabel('操作原因',{exact:true}).fill('虚构验证：明确永久删除两个节点文件并保留历史');
  await dialog.getByRole('checkbox').check();await capture('cleanup-confirmation.png');
  fixture.work=await browserWrite('ui-cleanup','/api/v1/manage/packages/'+ready.packageId+'/cleanup','POST',()=>dialog.getByRole('button',{name:'确认清理文件',exact:true}).click());await save();
 }else fixture.work=fixture.operations['ui-cleanup'].value;
 await stage(phase);
 phase='durable cleanup completion with download and version history retained';
 const status=await until(()=>get('/api/v1/manage/packages/'+ready.packageId+'/cleanup'),r=>r.state==='Cleaned');assert.equal(status.workId,fixture.work.id);
 const work=await get('/api/v1/manage/package-work/'+fixture.work.id);assert.equal(work.state,'Completed');assert.equal(work.completedItems,2);
 const old=await get('/api/v1/manage/releases/'+ready.id);assert.equal(old.state,'Disabled');assert.equal(old.version,'1.0.0');assert.equal(old.changeSummary,'仅供包清理流程验证');
 const audit=await get('/api/v1/manage/audit-events?softwareId='+fixture.software.id);assert.ok(audit.items.some(x=>x.packageId===ready.packageId&&x.endedAt));
 const denied=await call('/api/v1/packages/'+ready.packageId+'/content');assert.ok(denied.status>=400);
 await page.getByRole('button',{name:'重新核对清理条件',exact:true}).click();await expect(page.getByText('两个节点的包文件均已清理，历史已保留。',{exact:true})).toBeVisible();await expect(page.getByText('清理完成',{exact:true})).toBeVisible();await expect(page.getByRole('button',{name:'重新核对清理条件',exact:true})).toBeEnabled();await capture('cleanup-completed-history.png');assert.deepEqual(errors,[]);await stage(phase);
 fixture.verified=true;await save();await writeFile(join(results,'summary.json'),JSON.stringify({verifiedAt:now(),commit:identity.releaseCommit,project:identity.project,platform:identity.platform,stages,screenshots,scope:'Real Linux HTTPS/API/PostgreSQL/RabbitMQ/Nginx/browser; fictional package only. No production acceptance.'},null,2)+'\n');
 console.log('Package cleanup and retained history verified; credentials remain only in the owned demo.');
}catch(e){if(page)await page.screenshot({path:join(results,'failure-page.png'),fullPage:true,mask:[page.locator('input[type=password]')]}).catch(()=>{});await writeFile(join(results,'failure.json'),JSON.stringify({phase,name:e.name,message:String(e.message).replace(/[A-Za-z0-9_-]{32,}/g,'[redacted]'),stages},null,2)+'\n');console.error('Cleanup verification stopped at '+phase+'; inspect sanitized failure.json.');process.exitCode=1;}
finally{await browser?.close();agent.destroy();}
