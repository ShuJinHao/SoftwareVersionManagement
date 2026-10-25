import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Explicit fictional full deployment only. All authorization checks use real HTTPS and PostgreSQL.
const args=process.argv.slice(2),resume=args.at(-1)==='--resume',displayOnly=args.at(-1)==='--display-only';
if(resume||displayOnly)args.pop();
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw Error('Usage: eng/node deploy/linux/verify-roles.mjs --deployment folder --results folder [--resume|--display-only]');
const deployment=resolve(args[1]),results=resolve(args[3]),config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8')),identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
const rel=relative(join(root,'artifacts/test-results'),results);
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-f0-9]{12}$/.test(identity.project)||!rel||rel.startsWith('..')||new URL(config.url).protocol!=='https:')throw Error('Only an owned full demo and repository evidence directory are allowed.');
await mkdir(results,{recursive:true});
const agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true}),fixturePath=join(deployment,'roles-fixture.json'),jars=new Map(),screenshots=[],stages=[];
let fixture,browser,currentPage,phase='prepare',actor='admin';
const now=()=>new Date().toISOString(),save=()=>writeFile(fixturePath,JSON.stringify(fixture,null,2)+'\n',{mode:0o600});
async function call(path,{method='GET',body,bearer,headers={}}={}){
 const jar=jars.get(actor)??new Map();jars.set(actor,jar);
 const bytes=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
 return await new Promise((accept,reject)=>{const req=https(new URL(path,config.url),{agent,method,headers:{...(bearer?{Authorization:'Bearer '+bearer}:{Cookie:[...jar].map(([k,v])=>k+'='+v).join('; ')}),...(bytes?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':bytes.length}:{}),...headers}},response=>{
  if(!bearer)for(const value of response.headers['set-cookie']??[]){const pair=value.split(';')[0],i=pair.indexOf('=');jar.set(pair.slice(0,i),pair.slice(i+1));}
  const chunks=[];let size=0;response.on('data',chunk=>{size+=chunk.length;if(size>4*1024*1024)req.destroy(Error('Response limit'));else chunks.push(chunk);});response.on('error',reject);response.on('end',()=>{const bytes=Buffer.concat(chunks);let data=null;try{data=JSON.parse(bytes.toString());}catch{}accept({status:response.statusCode,bytes,data});});
 });const timer=setTimeout(()=>req.destroy(Error('HTTPS budget exceeded')),20000);req.once('close',()=>clearTimeout(timer));req.on('error',reject);if(bytes)req.write(bytes);req.end();});
}
function checked(r,path){if(![200,201,202,204,206].includes(r.status))throw Error('HTTP '+r.status+' '+(r.data?.code??'')+' at '+path.split('?')[0]);return r.data;}
const get=async path=>checked(await call(path),path);
async function login(key,password){actor=key;jars.set(key,new Map());const p=key==='admin'?config:fixture.people[key];const s=await get('/api/v1/session');return checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:p.employeeNo,password},headers:{'X-CSRF-TOKEN':s.csrfToken}}),'login');}
async function operation(name,action){const old=fixture.operations[name];if(old?.state==='done')return old.value;if(old)throw Error('Unconfirmed operation '+name+' requires original-operation verification.');fixture.operations[name]={state:'pending'};await save();const value=await action();fixture.operations[name]={state:'done',value};await save();return value;}
async function write(name,path,body,method='POST'){
 return operation(name,async()=>{const s=await get('/api/v1/session'),key=randomUUID();Object.assign(fixture.operations[name],{path,method,body,key,actor});await save();return checked(await call('/api/v1/manage/'+path,{method,body,headers:{'X-CSRF-TOKEN':s.csrfToken,'Idempotency-Key':key}}),path);});
}
const role=n=>'00000000-0000-4000-a000-'+String(n).padStart(12,'0'),assignment=(n,scopes)=>({roleId:role(n),scopes}),scope=(kind,resourceId=null)=>({kind,resourceId});
async function authorize(name,id,assignments,directPermissions=[]){const auth=await get('/api/v1/manage/subjects/'+id+'/authorization');return write(name,'subjects/'+id+'/authorization',{assignments,directPermissions,expectedRevision:auth.revision,reason:'虚构演示：明确角色与负责范围'},'PUT');}
async function stage(name){stages.push(name);console.log('Verified roles: '+name);}
async function until(action,predicate){const end=Date.now()+60000;while(Date.now()<end){const r=await action();if(predicate(r))return r;await new Promise(r=>setTimeout(r,300));}throw Error('Bounded wait at '+phase);}
function report(i,release,seq){return{streamEpoch:i.streamEpoch,reportSeq:seq,reportedAt:now(),installationState:release?'Installed':'NotInstalled',installedReleaseId:release?.id??null,installedVersion:release?.version??null,installedAt:null,runningState:'Stopped',reportedIps:[i.ip],databaseState:{mode:'None',items:[]}};}
async function machine(name,path,bearer,body){return operation(name,async()=>{const key=randomUUID();Object.assign(fixture.operations[name],{path,body,key});await save();return checked(await call(path,{method:'POST',bearer,body,headers:{'Idempotency-Key':key}}),path);});}
try{
 try{fixture=JSON.parse(await readFile(fixturePath,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 if(fixture){if(!(resume||displayOnly)||fixture.project!==identity.project||fixture.commit!==identity.releaseCommit)throw Error('Existing fixture requires matching --resume or --display-only.');if(displayOnly&&!fixture.verified)throw Error('Display-only requires completed verification.');}
 else{if(resume||displayOnly)throw Error('Missing checkpoint.');fixture={project:identity.project,commit:identity.releaseCommit,operations:{},people:{},prepared:false,verified:false};await save();}
 for(const [name,op]of Object.entries(fixture.operations))if(op.state!=='done')throw Error('Unconfirmed operation '+name+'; no automatic repeat.');
 if(!fixture.prepared){
  let s=await login('admin',fixture.operations.password?config.password:config.initialPassword);
  if(!fixture.operations.password)await operation('password',async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':s.csrfToken}}),'first password'));
  s=await get('/api/v1/session');fixture.adminId=s.subjectId;await save();
  await authorize('setup-roles',s.subjectId,[assignment(1,[scope('Site')]),assignment(2,[scope('Site')]),assignment(3,[scope('Site')])]);
  fixture.software=await write('software','software',{name:'虚构演示 · 共用控制程序',category:'UpperComputer'});
  fixture.processes=[];fixture.devices=[];fixture.hardware=[];fixture.positions=[];fixture.instances=[];
  for(const [index,name]of ['A','B'].entries()){
   const p=await write('process-'+name,'processes',{name:'虚构工序 '+name});fixture.processes.push(p);
   const d=await write('device-'+name,'devices',{processId:p.id,name:'虚构设备 '+name,assetNumber:'DEMO-'+name});fixture.devices.push(d);
   const h=await write('hardware-'+name,'devices/'+d.id+'/hardware',{name:'控制工控机 '+name,type:'IPC',managementIp:'192.0.2.'+(10+index)});fixture.hardware.push(h);
   const pos=await write('position-'+name,'hardware/'+h.id+'/software-bindings',{softwareId:fixture.software.id,reason:'演示跨工序共用软件'});fixture.positions.push(pos);
   const grantSecret=fixture['grantSecret'+name]??=randomBytes(32).toString('base64url'),secret=fixture['secret'+name]??=randomBytes(32).toString('base64url');await save();
   const g=await write('grant-'+name,'enrollment-grants',{softwareId:fixture.software.id,deviceIds:[d.id],hardwareBindingId:pos.id,hardwareBindingRevision:pos.revision,expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:grantSecret,reason:'虚构固定位置登记'});
   const registered=await machine('register-'+name,'/api/v1/enrollment/instances',g.id+'.'+grantSecret,{softwareId:fixture.software.id,deviceId:d.id,installationKey:randomUUID(),secretMaterial:secret});
   const bearer=registered.credentialId+'.'+secret,stream=await machine('stream-'+name,'/api/v1/client/report-streams',bearer,{expectedEpoch:0}),i={id:registered.instanceId,bearer,streamEpoch:stream.streamEpoch,ip:'192.0.2.'+(80+index)};fixture.instances.push(i);
   await machine('report-'+name,'/api/v1/client/status-reports',bearer,report(i,null,1));
  }
  for(const [key,employeeNo,displayName]of [['engineer','DEMO-ENGINEER','演示工程师'],['operations','DEMO-OPERATIONS','演示运维'],['reader','DEMO-READER','演示只读人员']]){
   fixture.people[key]??={employeeNo,displayName,temporaryPassword:randomBytes(24).toString('base64url')+'!9Aa',password:randomBytes(24).toString('base64url')+'!8Bb'};await save();
   const p=fixture.people[key];p.user=await write('person-'+key,'users',{employeeNo,displayName,temporaryPassword:p.temporaryPassword});await save();
  }
  await authorize('engineer-role',fixture.people.engineer.user.id,[assignment(2,[scope('Process',fixture.processes[0].id),scope('Software',fixture.software.id)])]);
  await authorize('operations-role',fixture.people.operations.user.id,[assignment(3,[scope('Device',fixture.devices[0].id)])]);
  await authorize('reader-role',fixture.people.reader.user.id,[assignment(4,[scope('Process',fixture.processes[1].id)])]);
  // Product publishing is explicit and temporary for demo preparation, never inferred from a field role.
  await authorize('setup-product',fixture.adminId,[assignment(1,[scope('Site')]),assignment(2,[scope('Site')])],['software.read','release.upload','release.publish'].map(operation=>({softwareId:fixture.software.id,operation})));
  phase='formal package for scoped download';
  const bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64'),sha256=createHash('sha256').update(bytes).digest('hex');
  const created=await write('release','software/'+fixture.software.id+'/releases',{changeLevel:'Patch',changeSummary:'角色范围演示包',changeReason:'虚构验证，不执行现场安装',package:{fileName:'roles-demo.zip',sizeBytes:bytes.length,sha256}});
  await operation('upload',async()=>checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'upload'));
  const ready=fixture.operations.publish?.value??await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
  await machine('installed-evidence','/api/v1/client/status-reports',fixture.instances[0].bearer,report(fixture.instances[0],ready,2));
  const evidence=(await get('/api/v1/manage/releases/'+ready.id+'/test-evidence')).items[0];
  fixture.release=await write('publish','releases/'+ready.id+'/publish',{testEvidenceId:evidence.id,publishReason:'演示共享正式版本',publishConclusion:'仅模拟协议事实',expectedRevision:ready.revision});
  await authorize('admin-final',fixture.adminId,[assignment(1,[scope('Site')])]);
  for(const key of ['engineer','operations','reader']){const p=fixture.people[key];const s=await login(key,fixture.operations['password-'+key]?p.password:p.temporaryPassword);if(!fixture.operations['password-'+key])await operation('password-'+key,async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:p.temporaryPassword,newPassword:p.password},headers:{'X-CSRF-TOKEN':s.csrfToken}}),'first password'));}
  fixture.prepared=true;await save();await stage('four named roles, explicit scopes, two processes sharing one software, real formal package');
 }
 if(!displayOnly){
  phase='real HTTP effective scope and product authorization';
  for(const [key,index]of [['engineer',0],['operations',0],['reader',1]]){
   await login(key,fixture.people[key].password);
   const devices=await get('/api/v1/manage/workspace/devices');assert.equal(devices.totalCount,1);assert.equal(devices.items[0].device.id,fixture.devices[index].id);
   const installations=await get('/api/v1/manage/workspace/installations?softwareId='+fixture.software.id);assert.equal(installations.totalCount,1);assert.equal(installations.items[0].device.id,fixture.devices[index].id);
   assert.equal((await call('/api/v1/manage/devices/'+fixture.devices[1-index].id)).status,404);
   assert.equal((await call('/api/v1/manage/permission-options')).status,403);
   const release=await get('/api/v1/manage/releases/'+fixture.release.id);assert.equal(release.state,'Formal');
   const download=await call('/api/v1/packages/'+fixture.release.packageId+'/content');assert.equal(download.status,200);assert.equal(createHash('sha256').update(download.bytes).digest('hex'),createHash('sha256').update(Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64')).digest('hex'));
   const s=await get('/api/v1/session');const denied=await call('/api/v1/manage/releases/'+fixture.release.id+'/disable',{method:'POST',body:{reason:'现场角色不能停用共享产品',expectedRevision:release.revision},headers:{'X-CSRF-TOKEN':s.csrfToken,'Idempotency-Key':randomUUID()}});assert.equal(denied.status,404);
   const auth=await get('/api/v1/session/authorization');assert.equal(auth.directPermissions.length,0);
  }
  await stage(phase);
 }
 process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');
 const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
 browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});
 for(const key of ['admin','engineer','operations','reader']){
  phase='browser '+key;const p=key==='admin'?config:fixture.people[key],context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage();currentPage=page;page.setDefaultTimeout(15000);const errors=[];page.on('pageerror',e=>errors.push(e.name));
  await page.goto(config.url+'/login');await page.getByLabel('工号',{exact:true}).fill(p.employeeNo);await page.getByLabel('密码',{exact:true}).fill(p.password);await page.getByRole('button',{name:'登录',exact:true}).click();await expect(page.getByRole('navigation',{name:'主导航'})).toBeVisible();
  await page.goto(config.url+'/workspace?view=software&softwareId='+fixture.software.id);await expect(page.locator('.installation-table')).toContainText(fixture.software.name);await expect(page.locator('.installation-table tbody tr')).toHaveCount(key==='admin'?2:1);
  if(key==='engineer')await expect(page.locator('.installation-table')).not.toContainText('控制工控机 B');
  if(key==='reader')await expect(page.locator('.installation-table')).not.toContainText('控制工控机 A');
  await page.screenshot({path:join(results,key+'-workspace.png'),fullPage:true});screenshots.push(key+'-workspace.png');
  await page.locator('.account-button').click();await page.getByRole('button',{name:'我的权限',exact:true}).click();await expect(page.getByRole('dialog',{name:'我的权限'})).toContainText(key==='admin'?'平台管理员':key==='engineer'?'程序工程师':key==='operations'?'现场运维':'只读人员');
  await page.screenshot({path:join(results,key+'-authorization.png'),fullPage:true});screenshots.push(key+'-authorization.png');await page.getByRole('button',{name:'关闭我的权限'}).click();
  if(key==='admin'){
   await page.goto(config.url+'/system');await page.getByRole('button',{name:'角色',exact:true}).click();await expect(page.getByRole('region',{name:'角色列表'})).toContainText('程序工程师');
   await page.screenshot({path:join(results,'admin-roles.png'),fullPage:true});screenshots.push('admin-roles.png');
   const row=page.locator('tbody tr').filter({hasText:'只读人员'});await row.getByRole('button',{name:'编辑',exact:true}).click();await expect(page.getByRole('dialog')).toContainText('1 名已分配人员');
   if(!displayOnly&&!fixture.operations['ui-role']){
    const dialog=page.getByRole('dialog');await dialog.getByLabel('操作原因',{exact:true}).fill('验证集中角色维护：保持只读职责');
    await operation('ui-role',async()=>{const path='/api/v1/manage/roles/'+role(4);const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()==='PATCH');const response=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()==='PATCH');await dialog.getByRole('button',{name:'保存角色',exact:true}).click();const request=await sent;Object.assign(fixture.operations['ui-role'],{path,method:'PATCH',body:request.postDataJSON(),key:request.headers()['idempotency-key']});await save();const r=await response;if(!r.ok())throw Error('Role browser write HTTP '+r.status());return r.json();});
    await expect(page.getByRole('dialog')).not.toBeVisible();
   }else await page.getByRole('button',{name:'关闭',exact:true}).click();
   await page.getByRole('button',{name:'人员',exact:true}).click();await page.getByRole('button',{name:'演示工程师',exact:true}).click();await page.getByRole('button',{name:'分配角色与范围',exact:true}).click();
   await expect(page.getByRole('dialog')).toContainText('虚构工序 A');await expect(page.getByRole('dialog')).toContainText('角色的独立产品范围');
   await page.screenshot({path:join(results,'admin-assignments.png'),fullPage:true});screenshots.push('admin-assignments.png');
   if(!displayOnly&&!fixture.operations['ui-assignment']){
    const dialog=page.getByRole('dialog');await dialog.getByRole('button',{name:'移除此分配',exact:true}).click();
    const choose=async(caption,id)=>{const select=dialog.getByRole('combobox',{name:caption,exact:true});await select.selectOption(id);await select.locator('..').getByRole('button',{name:'添加',exact:true}).click();};
    await choose('角色',role(2));await choose('负责现场',fixture.processes[0].id);
    await dialog.getByText('此角色的独立产品范围',{exact:true}).click();await choose('角色负责的产品',fixture.software.id);
    await dialog.getByLabel('操作原因',{exact:true}).fill('浏览器明确选择工程师、工序及产品，保持原负责范围');
    await operation('ui-assignment',async()=>{const path='/api/v1/manage/subjects/'+fixture.people.engineer.user.id+'/authorization';const sent=page.waitForRequest(r=>new URL(r.url()).pathname===path&&r.method()==='PUT');const response=page.waitForResponse(r=>new URL(r.url()).pathname===path&&r.request().method()==='PUT');await dialog.getByRole('button',{name:'保存',exact:true}).click();const request=await sent;Object.assign(fixture.operations['ui-assignment'],{path,method:'PUT',body:request.postDataJSON(),key:request.headers()['idempotency-key']});await save();const r=await response;if(!r.ok())throw Error('Authorization browser write HTTP '+r.status());return r.json();});
    await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.locator('.detail')).toContainText('虚构工序 A');
   }else await page.getByRole('button',{name:'关闭',exact:true}).click();
  }else{
   await page.goto(config.url+'/workspace?deviceId='+fixture.devices[key==='reader'?1:0].id);await expect(page.getByRole('heading',{name:'控制工控机 '+(key==='reader'?'B':'A'),exact:true})).toBeVisible();
   assert.equal(await page.getByRole('button',{name:'关联软件',exact:true}).count(),key==='engineer'?1:0);
   const select=page.locator('.installation-table tbody input[type=checkbox]').first();if(key==='operations'){await expect(select).toBeEnabled();await select.check();await expect(page.getByRole('button',{name:'更新所选软件',exact:true})).toBeEnabled();}else await expect(select).toBeDisabled();
   await page.goto(config.url+'/system');await expect(page.getByText('当前账号没有人员管理权限',{exact:false})).toBeVisible();
  }
  assert.deepEqual(errors,[]);await context.close();await stage(phase);
 }
 fixture.verified=true;await save();
 await writeFile(join(results,'summary.json'),JSON.stringify({verifiedAt:now(),commit:identity.releaseCommit,project:identity.project,url:config.url,platform:identity.platform,stages,screenshots,scope:'Fictional users, real HTTPS/PostgreSQL/RabbitMQ/Nginx; no GitLab or manufacturer acceptance.'},null,2)+'\n');
 console.log('Role scope demo verified; private login materials remain in the owned deployment only.');
}catch(e){if(currentPage)await currentPage.screenshot({path:join(results,'failure-page.png'),fullPage:true,mask:[currentPage.locator('input[type=password],.grant-material input')]}).catch(()=>{});await writeFile(join(results,'failure.json'),JSON.stringify({phase,name:e.name,message:String(e.message).replace(/[A-Za-z0-9_-]{32,}/g,'[redacted]'),stages},null,2)+'\n');console.error('Roles demo stopped at '+phase+'; inspect sanitized failure.json.');process.exitCode=1;}
finally{await browser?.close();agent.destroy();}
