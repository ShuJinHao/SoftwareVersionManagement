import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Explicit fictional demo, real HTTPS/APIs and browser. Never runs on a production deployment.
const args=process.argv.slice(2), resume=args.at(-1)==='--resume', displayOnly=args.at(-1)==='--display-only';
if(resume||displayOnly)args.pop();
if(args.length!==4||args[0]!=='--deployment'||args[2]!=='--results')throw new Error('Usage: eng/node deploy/linux/verify-hardware.mjs --deployment folder --results folder [--resume|--display-only]');
const deployment=resolve(args[1]),results=resolve(args[3]);
const config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8')),identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
const resultPath=relative(join(root,'artifacts/test-results'),results);
if(!identity.demo||identity.mode!=='full'||!/^svm-demo-[a-z0-9]+$/.test(identity.project)||!resultPath||resultPath.startsWith('..')||new URL(config.url).protocol!=='https:')throw new Error('Only an owned full demo and repository evidence folder are accepted.');
await mkdir(results,{recursive:true});
const fixturePath=join(deployment,'hardware-fixture.json'),agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true});
const cookies=new Map(),screenshots=[],stages=[];let fixture,browser,page,phase='prepare';
const delay=ms=>new Promise(r=>setTimeout(r,ms)),now=()=>new Date().toISOString();
const save=()=>writeFile(fixturePath,JSON.stringify(fixture,null,2)+'\n',{mode:0o600});
async function call(path,{method='GET',body,bearer,headers={}}={}){
 const bytes=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
 return await new Promise((accept,reject)=>{
  const req=https(new URL(path,config.url),{agent,method,headers:{...(bearer?{Authorization:'Bearer '+bearer}:{Cookie:[...cookies].map(([k,v])=>k+'='+v).join('; ')}),...(bytes?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':bytes.length}:{}),...headers}},response=>{
   if(!bearer)for(const value of response.headers['set-cookie']??[]){const pair=value.split(';')[0],i=pair.indexOf('=');cookies.set(pair.slice(0,i),pair.slice(i+1));}
   const chunks=[];let size=0;response.on('data',chunk=>{size+=chunk.length;if(size>4*1024*1024)req.destroy(new Error('Response limit'));else chunks.push(chunk);});response.on('error',reject);
   response.on('end',()=>{const bytes=Buffer.concat(chunks);let data=null;try{data=JSON.parse(bytes.toString());}catch{}accept({status:response.statusCode,bytes,data});});
  });const timer=setTimeout(()=>req.destroy(new Error('HTTPS budget exceeded')),20000);req.once('close',()=>clearTimeout(timer));req.on('error',reject);if(bytes)req.write(bytes);req.end();
 });
}
function checked(response,operation){if(![200,201,202,204,206].includes(response.status))throw new Error('HTTP '+response.status+' '+(response.data?.code??'')+' at '+operation);return response.data;}
const get=async path=>checked(await call(path),path.split('?')[0]);
async function login(password){const s=await get('/api/v1/session');return checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password},headers:{'X-CSRF-TOKEN':s.csrfToken}}),'login');}
// Persist completed work for safe continuation. An unconfirmed write stops continuation rather than being retried.
async function operation(name,action){const old=fixture.operations[name];if(old?.state==='done')return old.value;if(old)throw new Error('Unconfirmed operation '+name+' requires original-operation verification before continuation.');fixture.operations[name]={state:'pending'};await save();const value=await action();fixture.operations[name]={state:'done',value};await save();return value;}
async function write(name,path,body,method='POST'){
 return await operation(name,async()=>{const s=await get('/api/v1/session'),key=randomUUID();Object.assign(fixture.operations[name],{path,method,body,key});await save();return checked(await call('/api/v1/manage/'+path,{method,body,headers:{'X-CSRF-TOKEN':s.csrfToken,'Idempotency-Key':key}}),path);});
}
async function machine(name,path,bearer,body,key=randomUUID()){return await operation(name,async()=>checked(await call('/api/v1/client/'+path,{method:'POST',body,bearer,headers:{'Idempotency-Key':key}}),path));}
async function until(action,predicate,seconds=60){const end=Date.now()+seconds*1000;while(Date.now()<end){const v=await action();if(predicate(v))return v;await delay(300);}throw new Error('Bounded wait at '+phase);}
async function stage(name){stages.push(name);console.log('Verified hardware: '+name);}
async function screenshot(name){await page.screenshot({path:join(results,name+'.png'),fullPage:true,mask:[page.locator('input[type=password],.grant-material input')]});screenshots.push(name+'.png');}
async function uiWrite(name,path,action){return await operation(name,async()=>{const response=page.waitForResponse(r=>new URL(r.url()).pathname===path&&['POST','PUT','PATCH'].includes(r.request().method()),{timeout:20000});await action();const r=await response;if(!r.ok())throw new Error('Browser write '+name+' HTTP '+r.status());return await r.json();});}
function report(i,release,seq){return {streamEpoch:i.streamEpoch,reportSeq:seq,reportedAt:now(),installationState:release?'Installed':'NotInstalled',installedReleaseId:release?.id??null,installedVersion:release?.version??null,installedAt:null,runningState:'Stopped',reportedIps:[i.ip],databaseState:{mode:'None',items:[]}};}
try{
 try{fixture=JSON.parse(await readFile(fixturePath,'utf8'));}catch(e){if(e.code!=='ENOENT')throw e;}
 if(fixture){if(!(resume||displayOnly)||fixture.project!==identity.project||fixture.commit!==identity.releaseCommit)throw new Error('Existing fixture requires explicit matching --resume or --display-only.');if(displayOnly&&!fixture.verified)throw new Error('Display-only requires completed facts.');}
 else{if(resume||displayOnly)throw new Error('No checkpoint.');fixture={project:identity.project,commit:identity.releaseCommit,operations:{},prepared:false,verified:false};await save();}
 for(const [name,op]of Object.entries(fixture.operations))if(op.state!=='done')throw new Error('Inspect unconfirmed operation '+name+'; no automatic re-execution.');
 if(!fixture.prepared){
  let s=await login(fixture.operations.password?config.password:config.initialPassword);
  if(!fixture.operations.password)await operation('password',async()=>checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':s.csrfToken}}),'first password'));
  s=await get('/api/v1/session');let user=await get('/api/v1/manage/users/'+s.subjectId);
  await write('asset-permissions','subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...['asset.read','asset.manage'].map(operation=>({softwareId:null,operation}))],expectedRevision:user.revision,reason:'明确虚构的硬件演示授权'},'PUT');
  fixture.process=await write('process','processes',{name:'虚构演示 · 装配工序'});
  fixture.software=await write('software','software',{name:'虚构演示 · 设备控制',category:'UpperComputer'});
  assert.match(fixture.process.code,/^PR-\d{6,}$/);assert.match(fixture.software.code,/^SW-\d{6,}$/);
  user=await get('/api/v1/manage/users/'+s.subjectId);
  await write('software-permissions','subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...['release.upload','release.publish','release.disable','enrollment.manage','deployment.create','deployment.control','audit.read'].map(operation=>({softwareId:fixture.software.id,operation}))],expectedRevision:user.revision,reason:'明确虚构的软件职责'},'PUT');
  fixture.devices=[];fixture.hardware=[];fixture.positions=[];fixture.instances=[];
  for(const [index,name]of ['A','B'].entries()){
   const d=await write('device-'+name,'devices',{processId:fixture.process.id,name:'虚构演示 · 设备 '+name,assetNumber:'DEMO-'+name});fixture.devices.push(d);assert.match(d.deviceNo,/^EQ-\d{6,}$/);
   const h=await write('hardware-'+name,'devices/'+d.id+'/hardware',{name:'控制工控机 '+name,type:'IPC',managementIp:'192.0.2.'+(10+index)});fixture.hardware.push(h);
   const p=await write('position-'+name,'hardware/'+h.id+'/software-bindings',{softwareId:fixture.software.id,reason:'明确虚构的安装位置'});fixture.positions.push(p);
   const grantSecret=fixture['grantSecret'+name]??=randomBytes(32).toString('base64url'),secret=fixture['secret'+name]??=randomBytes(32).toString('base64url');await save();
   const grant=await write('grant-'+name,'enrollment-grants',{softwareId:fixture.software.id,deviceIds:[d.id],hardwareBindingId:p.id,hardwareBindingRevision:p.revision,expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:grantSecret,reason:'虚构固定位置登记'});
   const registration=await operation('register-'+name,async()=>checked(await call('/api/v1/enrollment/instances',{method:'POST',bearer:grant.id+'.'+grantSecret,body:{softwareId:fixture.software.id,deviceId:d.id,installationKey:randomUUID(),secretMaterial:secret},headers:{'Idempotency-Key':randomUUID()}}),'register'));
   const bearer=registration.credentialId+'.'+secret,stream=await machine('stream-'+name,'report-streams',bearer,{expectedEpoch:0});
   const instance={id:registration.instanceId,bearer,streamEpoch:stream.streamEpoch,ip:'192.0.2.'+(80+index)};fixture.instances.push(instance);
   await machine('report-'+name,'status-reports',bearer,report(instance,null,1));
  }
  fixture.plc=await write('plc','devices/'+fixture.devices[0].id+'/hardware',{name:'虚构 PLC 主控',type:'PLC',managementIp:'192.0.2.20'});
  phase='real double-copy package, historical report and formal release';
  const bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64');fixture.sha256=createHash('sha256').update(bytes).digest('hex');
  const created=await write('release','software/'+fixture.software.id+'/releases',{changeLevel:'Patch',changeSummary:'硬件工作台虚构演示包',changeReason:'功能验收，不执行现场安装',package:{fileName:'hardware-demo.zip',sizeBytes:bytes.length,sha256:fixture.sha256}});
  await operation('upload',async()=>checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'upload'));
  const ready=fixture.operations.publish?.value??await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
  await machine('evidence-report','status-reports',fixture.instances[0].bearer,report(fixture.instances[0],ready,2));
  const evidence=(await get('/api/v1/manage/releases/'+ready.id+'/test-evidence')).items[0];
  fixture.release=await write('publish','releases/'+ready.id+'/publish',{testEvidenceId:evidence.id,publishReason:'明确虚构的演示发布',publishConclusion:'仅模拟协议，不代表厂家实际安装',expectedRevision:ready.revision});
  const download=await call('/api/v1/packages/'+fixture.release.packageId+'/content');assert.equal(download.status,200);assert.deepEqual(download.bytes,bytes);
  fixture.prepared=true;await save();await stage(phase);
 }else await login(config.password);
 process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');
 const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
 browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});
 page=await browser.newPage({viewport:{width:1440,height:1000}});page.setDefaultTimeout(15000);
 const consoleErrors=[];page.on('pageerror',e=>consoleErrors.push(e.name));
 await page.goto(config.url+'/login');await screenshot('01-login');await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);await page.getByRole('button',{name:'登录',exact:true}).click();await expect(page.getByRole('navigation',{name:'主导航'})).toBeVisible();
 assert.deepEqual(await page.getByRole('navigation',{name:'主导航'}).getByRole('link').allTextContents(),['软件工作台','系统管理']);
 const deviceUrl=config.url+'/workspace?processId='+fixture.process.id+'&deviceId='+fixture.devices[0].id;
 if(!displayOnly){
  phase='browser creates hardware and software at its fixed installation position';
  await page.goto(deviceUrl);await expect(page.getByRole('heading',{name:'控制工控机 A',exact:true})).toBeVisible();
  if(!fixture.operations['ui-hardware']){
   await page.getByRole('button',{name:'添加硬件',exact:true}).click();const dialog=page.getByRole('dialog');await dialog.getByLabel('名称',{exact:true}).fill('视觉工控机 A');await dialog.getByLabel(/^管理 IP（可选）/).fill('192.0.2.30');
   fixture.visualHardware=await uiWrite('ui-hardware','/api/v1/manage/devices/'+fixture.devices[0].id+'/hardware',()=>dialog.getByRole('button',{name:'确认',exact:true}).click());await save();
  }else fixture.visualHardware=fixture.operations['ui-hardware'].value;
  await expect(page.getByRole('dialog')).not.toBeVisible();
  if(!fixture.operations['ui-position']){
   const section=page.locator('.hardware-section').filter({has:page.getByRole('heading',{name:'视觉工控机 A',exact:true})});await section.getByRole('button',{name:'关联软件',exact:true}).click();const dialog=page.getByRole('dialog');await dialog.getByLabel(/^软件来源/).selectOption('new');await dialog.getByLabel('软件名称',{exact:true}).fill('虚构演示 · 视觉检查');await dialog.getByLabel(/^类型/).selectOption('Vision');await dialog.getByLabel('操作原因',{exact:true}).fill('在指定工控机新建视觉软件');
   fixture.visualPosition=await uiWrite('ui-position','/api/v1/manage/hardware/'+fixture.visualHardware.id+'/software-bindings',()=>dialog.getByRole('button',{name:'确认',exact:true}).click());await save();
  }else fixture.visualPosition=fixture.operations['ui-position'].value;
  await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.getByRole('button',{name:'虚构演示 · 视觉检查',exact:true})).toBeVisible();await stage(phase);
  // A shared program on another IPC demonstrates many-to-many without changing any installed identity.
  await write('second-program','hardware/'+fixture.visualHardware.id+'/software-bindings',{softwareId:fixture.software.id,reason:'虚构多软件工控机、共享版本库'});
  phase='software reverse lookup, real source and formal download';
  await page.goto(deviceUrl);await page.locator('.installation-table').getByRole('button',{name:fixture.software.name,exact:true}).first().click();await expect(page.locator('.content-heading h2')).toHaveText(fixture.software.name);
  await expect(page.locator('.installation-table tbody tr')).toHaveCount(3);await expect(page.locator('.installation-table')).toContainText('控制工控机 B');await screenshot('03-software-reverse');
  await page.getByRole('button',{name:'版本与来源',exact:true}).first().click();await expect(page.getByLabel('版本来源配置')).toContainText('尚未绑定 GitLab');
  await page.getByRole('button',{name:'1.0.0',exact:true}).click();await expect(page.getByRole('complementary',{name:'版本详情'})).toContainText('正式发布记录');await screenshot('04-release-source');await stage(phase);
  phase='browser deployment uses exactly the selected hardware-bound instance';
  if(!fixture.operations['ui-deployment']){
   await page.goto(config.url+'/workspace?deviceId='+fixture.devices[1].id);const row=page.locator('.installation-table tbody tr').filter({hasText:fixture.software.name});await expect(row).toContainText('尚未安装');await row.getByRole('checkbox').check();await page.getByRole('button',{name:'更新所选软件',exact:true}).click();
   await expect(page.getByText('已带入工作台勾选的安装位置',{exact:false})).toBeVisible();await page.getByLabel(/^正式目标版本/).selectOption(fixture.release.id);await page.getByLabel('使用配置的下一时段',{exact:true}).uncheck();
   await page.getByLabel('UTC 开始',{exact:true}).fill(new Date(Date.now()-120000).toISOString().slice(0,16));await page.getByLabel('UTC 最晚开始',{exact:true}).fill(new Date(Date.now()+3600000).toISOString().slice(0,16));await page.getByLabel('投放原因',{exact:true}).fill('仅更新所选控制工控机 B，模拟接入协议');
   fixture.update=await uiWrite('ui-deployment','/api/v1/manage/deployments',()=>page.getByRole('button',{name:'封存目标并创建更新投放',exact:true}).click());await save();await expect(page.getByText('投放已登记',{exact:false})).toBeVisible();
  }else fixture.update=fixture.operations['ui-deployment'].value;
  const tasks=(await until(()=>get('/api/v1/manage/deployments/'+fixture.update.id+'/tasks'),r=>r.items.length===1&&['Available','Claimed','AwaitingResult','Succeeded'].includes(r.items[0].state))).items;
  assert.equal(tasks[0].instanceId,fixture.instances[1].id);assert.equal(tasks[0].hardwareBindingId,fixture.positions[1].id);fixture.taskId=tasks[0].id;await save();await screenshot('05-update-target');await stage(phase);
  phase='real claim, start permit, receipt and separate installed report';
  const i=fixture.instances[1],claim=await machine('claim','tasks/'+fixture.taskId+'/claim',i.bearer,{});
  await machine('start','attempts/'+claim.attemptId+'/start',i.bearer,{stateReport:report(i,null,2),preflight:{downloadedSha256:fixture.sha256,dataProtectionConfirmed:true}});
  await machine('receipt','attempts/'+claim.attemptId+'/receipts',i.bearer,{eventId:randomUUID(),sequence:1,kind:'Terminal',result:'Succeeded'});
  await machine('installed-report','status-reports',i.bearer,report(i,fixture.release,3));
  const final=await get('/api/v1/manage/instances/'+i.id);assert.equal(final.lastSnapshot.installedReleaseId,fixture.release.id);assert.equal(final.hardwareBindingId,fixture.positions[1].id);
  fixture.verified=true;await save();await stage(phase);
 }
 phase='current workspace pages, permissions and account menu';
 await page.goto(deviceUrl);await expect(page.getByRole('heading',{name:'视觉工控机 A',exact:true})).toBeVisible();await screenshot('02-device-hardware');
 const plc=page.locator('.hardware-section').filter({has:page.getByRole('heading',{name:'虚构 PLC 主控',exact:true})});await expect(plc).toContainText('后续批次开放');await expect(plc.locator('table')).toHaveCount(0);
 await page.goto(config.url+'/workspace?deviceId='+fixture.devices[1].id);await expect(page.locator('.installation-table')).toContainText('1.0.0');await screenshot('06-installed-fact');
 await page.goto(config.url+'/instances?softwareId='+fixture.software.id+'&id='+fixture.instances[1].id);await expect(page).toHaveURL(/\/workspace\?/);await expect(page.locator('main')).toContainText('控制工控机 B');await screenshot('07-instance-detail');
 await page.getByRole('link',{name:'系统管理',exact:true}).click();await page.getByRole('button',{name:'查看当前权限',exact:true}).click();await expect(page.getByRole('region',{name:'当前有效权限'})).toContainText('asset.manage');await screenshot('08-permissions');
 await page.locator('.account-button').click();await page.getByRole('button',{name:'修改本人密码',exact:true}).click();await expect(page.getByRole('dialog')).toBeVisible();await page.getByRole('button',{name:'关闭改密',exact:true}).click();await expect(page).toHaveURL(/\/system$/);
 await page.setViewportSize({width:1280,height:720});await page.goto(deviceUrl);await expect(page.locator('.hardware-section')).toHaveCount(3);await screenshot('09-workspace-1280');
 const overflow=await page.evaluate(()=>({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth}));assert.ok(overflow.scroll<=overflow.width+1,'Page should scroll tables, not overflow the body');
 await page.getByRole('button',{name:'添加硬件',exact:true}).click();for(let n=0;n<9;n++){await page.keyboard.press('Tab');const focus=await page.getByRole('dialog').evaluate(el=>({inside:el.contains(document.activeElement),focused:document.hasFocus()}));assert.ok(!focus.focused||focus.inside,'Focused modal must retain keyboard navigation');}await page.getByRole('button',{name:'取消',exact:true}).click();assert.deepEqual(consoleErrors,[]);await stage(phase);
 await writeFile(join(results,'summary.json'),JSON.stringify({success:true,project:identity.project,platform:identity.platform,host:process.platform+'/'+process.arch,releaseCommit:identity.releaseCommit,releaseDirty:identity.releaseDirty,url:config.url,stages,screenshots,fixture:'Explicit fictional ledger; machine API simulation only',notCovered:['real GitLab','vendor installation','PLC archive','production HA']},null,2)+'\n');
 await writeFile(join(results,'pages.md'),'# 硬件软件工作台 · 真实 Linux 演示\n\n'+screenshots.map(x=>'!['+x+']('+join(results,x)+')').join('\n\n')+'\n');
}catch(e){if(page)await screenshot('failure-page').catch(()=>{});await writeFile(join(results,'failure.json'),JSON.stringify({phase,message:e instanceof Error?e.message:'Verification failed'},null,2)+'\n');console.error('Hardware verification stopped at '+phase+': '+(e instanceof Error?e.message:'Unknown error'));process.exitCode=1;}
finally{await browser?.close();agent.destroy();}
