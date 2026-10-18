import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

// Dedicated fictional data and real public APIs; faults live only in browser routes.
const args = process.argv.slice(2);
const displayOnly = args.at(-1) === '--display-only'; if (displayOnly) args.pop();
if (args.length !== 4 || args[0] !== '--deployment' || args[2] !== '--results')
  throw new Error('Usage: eng/node deploy/linux/verify-audit.mjs --deployment folder --results folder [--display-only]');
const deployment = resolve(args[1]), results = resolve(args[3]);
const config = JSON.parse(await readFile(join(deployment, 'operator.json'), 'utf8'));
const identity = JSON.parse(await readFile(join(deployment, 'deployment.json'), 'utf8'));
const resultPath = relative(join(root, 'artifacts/test-results'), results);
if (!identity.demo || identity.mode !== 'full' || !/^svm-demo-[a-z0-9]+$/.test(identity.project) ||
    !resultPath || resultPath.startsWith('..') || new URL(config.url).protocol !== 'https:')
  throw new Error('Only an owned full demo and project evidence folder are accepted.');
await mkdir(results, { recursive: true });
const fixturePath = join(deployment, 'audit-fixture.json');
const agent = new Agent({ ca: await readFile(resolve(deployment, config.caFile)), keepAlive: true });
const cookies = new Map(), stages = [], injections = [], screenshots = [];
let phase = 'prepare', browser, page, fixture;
const now = () => new Date().toISOString();
const delay = ms => new Promise(done => setTimeout(done, ms));
async function call(path, { method = 'GET', body, bearer, headers = {}, cookie = true } = {}) {
  const bytes = body === undefined ? null : Buffer.isBuffer(body) ? body : Buffer.from(JSON.stringify(body));
  return await new Promise((accept, reject) => {
    const req = https(new URL(path, config.url), { agent, method, headers: {
      ...(bearer ? { Authorization: 'Bearer ' + bearer } : cookie ? { Cookie: [...cookies].map(([k,v]) => k+'='+v).join('; ') } : {}),
      ...(bytes ? { 'Content-Type': Buffer.isBuffer(body) ? 'application/octet-stream' : 'application/json', 'Content-Length': bytes.length } : {}), ...headers,
    } }, response => {
      if (cookie && !bearer) for (const value of response.headers['set-cookie'] ?? []) {
        const pair = value.split(';')[0], i = pair.indexOf('='); cookies.set(pair.slice(0,i),pair.slice(i+1));
      }
      const chunks = []; let size = 0;
      response.on('data', chunk => { size += chunk.length; if (size > 4*1024*1024) req.destroy(new Error('Response limit')); else chunks.push(chunk); });
      response.on('error', reject);
      response.on('end', () => { const bytes = Buffer.concat(chunks); let data = null;
        try { data = JSON.parse(bytes.toString()); } catch { /* Content is never printed. */ }
        accept({ status: response.statusCode, headers: response.headers, bytes, data }); });
    });
    const timer = setTimeout(() => req.destroy(new Error('HTTPS budget exceeded')), 15000);
    req.once('close', () => clearTimeout(timer)); req.on('error', reject); if (bytes) req.write(bytes); req.end();
  });
}
function checked(response, operation) {
  if (![200,201,202,204,206].includes(response.status)) throw new Error('HTTP '+response.status+' '+(response.data?.code ?? '')+' at '+operation);
  return response.data;
}
const get = async path => checked(await call(path), path.split('?')[0]);
async function write(path, body, method = 'POST') {
  const session = await get('/api/v1/session');
  return checked(await call('/api/v1/manage/'+path, { method, body, headers: {
    'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID(),
  } }), path);
}
async function login(password) {
  const session = await get('/api/v1/session');
  return checked(await call('/api/v1/session', { method:'POST', body:{employeeNo:config.employeeNo,password}, headers:{'X-CSRF-TOKEN':session.csrfToken} }), 'login');
}
const machine = async (path,bearer,body,key) => checked(await call('/api/v1/client/'+path, {method:'POST',body,bearer,headers:key?{'Idempotency-Key':key}:{}}),path);
async function until(action, predicate, seconds = 45) {
  const end = Date.now()+seconds*1000;
  while(Date.now()<end) { const value=await action(); if(predicate(value)) return value; await delay(300); }
  throw new Error('Bounded wait at '+phase);
}
async function saveFixture() { await writeFile(fixturePath,JSON.stringify(fixture,null,2)+'\n',{mode:0o600}); }
async function complete(name) { stages.push(name); console.log('Verified audit: '+name); }
async function forward(route, url = new URL(route.request().url())) {
  assert.equal(url.origin,new URL(config.url).origin);
  const request=route.request(); return await call(url.pathname+url.search, {method:request.method(),body:request.postDataBuffer()??undefined,
    cookie:false,headers:{...await request.allHeaders(),'accept-encoding':'identity'}});
}
const responseOptions = response => ({status:response.status,body:response.bytes,headers:Object.fromEntries(Object.entries(response.headers)
  .filter(([,value])=>value!==undefined).map(([key,value])=>[key,Array.isArray(value)?value.join('\n'):String(value)]))});
async function screenshot(name) {
  await page.screenshot({path:join(results,name+'.png'),fullPage:true,mask:[page.locator('input[type=password], input[readonly]')]}); screenshots.push(name+'.png');
}
try {
  try { fixture=JSON.parse(await readFile(fixturePath,'utf8')); } catch(e) { if(e.code!=='ENOENT')throw e; }
  if(displayOnly) {
    if(!fixture?.verified || fixture.project!==identity.project || fixture.commit!==identity.releaseCommit)
      throw new Error('Display-only requires already verified matching facts; no unknown writes are retried.');
    await login(config.password);
  } else {
    if(fixture) throw new Error('Owned audit fixture already exists; inspect its checkpoint before any further writes.');
    fixture={format:1,project:identity.project,commit:identity.releaseCommit,prepared:false,verified:false}; await saveFixture();
    let session=await login(config.initialPassword);
    assert.equal(session.mustChangePassword,true);
    session=checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':session.csrfToken}}),'first password');
    let user=await get('/api/v1/manage/users/'+session.subjectId);
    await write('subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...['asset.read','asset.manage'].map(operation=>({softwareId:null,operation}))],expectedRevision:user.revision,reason:'明确虚构演示授权'},'PUT');
    fixture.process=await write('processes',{code:'AUDIT-P',name:'虚构演示工序'});
    fixture.software=[];
    for(const name of ['A','B','C']) fixture.software.push(await write('software',{code:'AUDIT-S-'+name,name:'虚构演示软件 '+name,category:name==='B'?'UpperComputer':'Vision'}));
    user=await get('/api/v1/manage/users/'+session.subjectId);
    await write('subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...fixture.software.flatMap(s=>['release.upload','release.publish','release.disable','enrollment.manage','deployment.create','deployment.control','audit.read'].map(operation=>({softwareId:s.id,operation})))],expectedRevision:user.revision,reason:'明确虚构的软件职责'},'PUT');
    const software=fixture.software[0], instances=[];fixture.devices=[];
    for(const [index,name] of ['A','B','C'].entries()) {
      const device=await write('devices',{processId:fixture.process.id,deviceNo:'AUDIT-D-'+name,name:'虚构演示设备 '+name});fixture.devices.push(device);
      await write('devices/'+device.id+'/software-bindings',{softwareId:software.id,reason:'虚构映射'});
      if(name==='C')continue;
      const grantSecret=randomBytes(32).toString('base64url'),secret=randomBytes(32).toString('base64url');
      const grant=await write('enrollment-grants',{softwareId:software.id,deviceIds:[device.id],expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:grantSecret,reason:'虚构实例登记'});
      const registration=checked(await call('/api/v1/enrollment/instances',{method:'POST',bearer:grant.id+'.'+grantSecret,body:{softwareId:software.id,deviceId:device.id,installationKey:randomUUID(),secretMaterial:secret},headers:{'Idempotency-Key':randomUUID()}}),'register');
      const bearer=registration.credentialId+'.'+secret, stream=await machine('report-streams',bearer,{expectedEpoch:0},randomUUID());let sequence=0;
      const report=release=>({streamEpoch:stream.streamEpoch,reportSeq:++sequence,reportedAt:now(),installationState:release?'Installed':'NotInstalled',installedReleaseId:release?.id??null,installedVersion:release?.version??null,installedAt:null,runningState:'Stopped',reportedIps:['192.0.2.'+(80+index)],databaseState:{mode:'None',items:[]}});
      await machine('status-reports',bearer,report(null));instances.push({id:registration.instanceId,device,bearer,report});
    }
    await write('devices/'+fixture.devices[0].id+'/software-bindings',{softwareId:fixture.software[1].id,reason:'同设备第二软件，尚未登记'});
    phase='real double-copy package and formal release';
    const bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64'),hash=createHash('sha256').update(bytes).digest('hex');
    const created=await write('software/'+software.id+'/releases',{changeLevel:'Patch',changeSummary:'明确虚构的页面演示包',changeReason:'功能展示，不执行现场安装',package:{fileName:'audit-demo.zip',sizeBytes:bytes.length,sha256:hash}});
    checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':(await get('/api/v1/session')).csrfToken}}),'upload');
    const ready=await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
    await machine('status-reports',instances[0].bearer,instances[0].report(ready));
    const evidence=(await get('/api/v1/manage/releases/'+ready.id+'/test-evidence')).items[0];
    fixture.release=await write('releases/'+ready.id+'/publish',{testEvidenceId:evidence.id,publishReason:'虚构演示发布',publishConclusion:'模拟安装上报，仅用于网页验收',expectedRevision:ready.revision});
    const downloaded=await call('/api/v1/packages/'+ready.packageId+'/content');assert.equal(downloaded.status,200);assert.deepEqual(downloaded.bytes,bytes);
    await complete(phase);
    phase='real update task and independent report';
    const target=instances[1];let selection=await write('target-selections',{softwareId:software.id,mode:'Explicit'});
    selection=await write('target-selections/'+selection.id+'/chunks/0',{instanceIds:[target.id]},'PUT');
    selection=await write('target-selections/'+selection.id+'/seal',{expectedRevision:selection.revision,chunkCount:1,expectedDistinctCount:1});
    fixture.deployment=await write('deployments',{softwareId:software.id,selectionId:selection.id,kind:'Update',targetReleaseId:ready.id,window:{notBefore:new Date(Date.now()-60000).toISOString(),latestStart:new Date(Date.now()+3600000).toISOString()},reason:'虚构演示投放，不代表现场安装'});
    const task=(await until(()=>get('/api/v1/manage/deployments/'+fixture.deployment.id+'/tasks'),r=>r.items[0]?.state==='Available')).items[0];
    const claim=await machine('tasks/'+task.id+'/claim',target.bearer,{},randomUUID());
    await machine('attempts/'+claim.attemptId+'/start',target.bearer,{stateReport:target.report(null),preflight:{downloadedSha256:hash,dataProtectionConfirmed:true}},randomUUID());
    await machine('attempts/'+claim.attemptId+'/receipts',target.bearer,{eventId:randomUUID(),sequence:1,kind:'Terminal',result:'Succeeded'});
    await machine('status-reports',target.bearer,target.report(ready));
    fixture.instances=instances.map(({id,device})=>({id,device}));fixture.prepared=true;await saveFixture();await complete(phase);
  }
  process.env.PLAYWRIGHT_BROWSERS_PATH??=join(root,'.cache/playwright-browsers');
  const {chromium,expect}=createRequire(join(root,'src/ui/svm-web/package.json'))('@playwright/test');
  browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});
  page=await browser.newPage({viewport:{width:1440,height:1000}}); page.setDefaultTimeout(10000);
  await page.goto(config.url+'/login');await screenshot('01-login');
  await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);
  await page.getByRole('button',{name:'登录',exact:true}).click();await page.getByRole('button',{name:'退出',exact:true}).waitFor();
  const [a,b]=fixture.software,[ia,ib]=fixture.instances;
  if(!displayOnly) {
    phase='software applied filters and delayed detail ownership';
    const paths=[];
    await page.route('**/api/v1/manage/software?*',async route=>{
      const url=new URL(route.request().url());paths.push(url.pathname+url.search);url.searchParams.set('pageSize','1');
      await route.fulfill(responseOptions(await forward(route,url)));
    });injections.push('Software pageSize reduced to 1 at the HTTPS boundary; real filtered rows and protected cursors retained.');
    await page.goto(config.url+'/software');await page.getByRole('button',{name:a.name,exact:true}).waitFor();
    await page.getByLabel('软件名称',{exact:true}).fill('虚构演示');await page.getByRole('button',{name:'查询',exact:true}).click();
    await expect(page.getByRole('button',{name:'下一页',exact:true})).toBeEnabled();
    await page.getByLabel('软件名称',{exact:true}).fill('尚未应用的筛选');await page.getByRole('button',{name:'下一页',exact:true}).click();
    await page.getByRole('button',{name:b.name,exact:true}).waitFor();assert.equal(new URL(paths.at(-1),config.url).searchParams.get('name'),'虚构演示');
    await page.unroute('**/api/v1/manage/software?*');await page.goto(config.url+'/software');await page.getByRole('button',{name:a.name,exact:true}).waitFor();
    let releaseOld;const gate=new Promise(done=>{releaseOld=done;});let receivedOld;const started=new Promise(done=>{receivedOld=done;});
    await page.route('**/api/v1/manage/software/'+a.id,async route=>{const r=await forward(route);receivedOld();await gate;await route.fulfill(responseOptions(r)).catch(()=>{});},{times:1});
    await page.getByRole('button',{name:a.name,exact:true}).click();await started;await page.getByRole('button',{name:b.name,exact:true}).click();
    await expect(page.locator('aside.detail h2')).toHaveText(b.name);releaseOld();await expect(page.locator('aside.detail h2')).toHaveText(b.name);
    injections.push('Delayed software A detail released after B selection.');await complete(phase);

    phase='frozen software edit and committed response loss';
    await page.getByRole('button',{name:a.name,exact:true}).click();await expect(page.locator('aside.detail h2')).toHaveText(a.name);await page.getByRole('button',{name:'修改软件资料',exact:true}).click();
    const dialog=page.locator('dialog');await expect(dialog).toContainText(a.id);
    // Native dialogs make background inert. Also simulate a concurrent selection directly,
    // verifying that the form cannot retarget even if application state changes.
    await page.locator('tbody button').filter({hasText:b.name}).evaluate(el=>el.click());
    await expect(dialog).toContainText(a.id);injections.push('Programmatic background selection while native modal is open.');
    await dialog.getByLabel('软件名称',{exact:true}).fill(a.name+'（已修订）');await dialog.getByLabel('操作原因',{exact:true}).fill('虚构演示：固定原对象');
    const attempts=[];let committed;
    await page.route('**/api/v1/manage/software/'+a.id,async route=>{
      if(route.request().method()!=='PATCH'){await route.continue();return;}
      attempts.push({key:route.request().headers()['idempotency-key'],body:route.request().postData()});
      const response=await forward(route);assert.equal(response.status,200);committed=response.data;
      if(attempts.length===1)await route.abort('failed');else await route.fulfill(responseOptions(response));
    });
    await dialog.getByRole('button',{name:'保存',exact:true}).click();await dialog.getByRole('button',{name:'核实原操作',exact:true}).waitFor();
    await expect(dialog).toContainText(a.id);await screenshot('02-software-unknown-result');
    await dialog.getByRole('button',{name:'核实原操作',exact:true}).click();await expect(dialog).toHaveCount(0);
    assert.equal(attempts.length,2);assert.deepEqual(attempts[1],attempts[0]);assert.equal(committed.id,a.id);assert.equal(committed.revision,a.revision+1);
    assert.equal((await get('/api/v1/manage/software/'+b.id)).name,b.name);a.name=committed.name;a.revision=committed.revision;
    await page.unroute('**/api/v1/manage/software/'+a.id);injections.push('A successful PATCH response dropped; manual verification uses original key and body.');await complete(phase);

    phase='instance lifecycle remains attached to original dialog target';
    await page.goto(config.url+'/instances?softwareId='+a.id);const row=name=>page.locator('tbody tr').filter({hasText:name});
    await row(ia.device.deviceNo).getByRole('button',{name:'查看实例'}).click();await page.getByRole('button',{name:'暂停 API 接入',exact:true}).click();
    await row(ib.device.deviceNo).locator('button').filter({hasText:'查看实例'}).evaluate(el=>el.click());
    await expect(page.locator('dialog')).toContainText(ia.id);await page.locator('dialog').getByLabel('操作原因',{exact:true}).fill('虚构演示接入暂停');await screenshot('03-instance-fixed-target');
    await page.locator('dialog').getByRole('button',{name:'确认提交',exact:true}).click();await expect(page.locator('dialog')).toHaveCount(0);
    assert.equal((await get('/api/v1/manage/instances/'+ia.id)).lifecycle,'Suspended');assert.equal((await get('/api/v1/manage/instances/'+ib.id)).lifecycle,'Active');await complete(phase);

    phase='recovery grant revocation stays with its issued instance';
    await row(ib.device.deviceNo).getByRole('button',{name:'查看实例'}).click();await page.getByRole('button',{name:'签发单次恢复许可',exact:true}).click();
    const expires=new Date(Date.now()+15*60000);const local=new Date(expires.getTime()-expires.getTimezoneOffset()*60000).toISOString().slice(0,16);
    await page.locator('dialog').getByLabel('有效期').fill(local);await page.locator('dialog').getByLabel('操作原因').fill('虚构演示恢复许可');
    await page.locator('dialog').getByRole('button',{name:'确认提交',exact:true}).click();await page.getByRole('button',{name:'隐藏秘密',exact:true}).click();
    await row(ia.device.deviceNo).getByRole('button',{name:'查看实例'}).click();await page.getByRole('button',{name:'撤销本次恢复许可',exact:true}).click();
    await expect(page.locator('dialog')).toContainText(ib.id);await page.locator('dialog').getByLabel('操作原因').fill('演示后撤销许可');
    await page.locator('dialog').getByRole('button',{name:'确认提交',exact:true}).click();await expect(page.locator('dialog')).toHaveCount(0);await complete(phase);
    fixture.verified=true;await saveFixture();
  }
  phase='current Linux pages and keyboard path';
  await page.goto(config.url+'/site');await page.getByRole('button',{name:new RegExp(fixture.process.name)}).click();
  await page.getByRole('button',{name:new RegExp(fixture.devices[0].name)}).click();await expect(page.locator('.device-detail')).toContainText(a.name);await screenshot('04-site');
  await page.goto(config.url+'/software');await page.getByRole('button',{name:a.name,exact:true}).click();await expect(page.locator('aside.detail h2')).toHaveText(a.name);await screenshot('05-software');
  await page.setViewportSize({width:1280,height:720});await page.getByRole('button',{name:'修改软件资料',exact:true}).click();
  for(let i=0;i<8;i++) {await page.keyboard.press('Tab');assert.ok(await page.locator('dialog').evaluate(el=>el.contains(document.activeElement)));}
  await page.keyboard.press('Escape');await expect(page.locator('dialog')).toHaveCount(0);await expect(page.getByRole('button',{name:'修改软件资料',exact:true})).toBeFocused();
  await page.setViewportSize({width:1440,height:1000});await page.goto(config.url+'/instances?softwareId='+a.id);
  await page.locator('tbody tr').filter({hasText:ib.device.deviceNo}).getByRole('button',{name:'查看实例'}).click();await expect(page.locator('.device-facts')).toContainText(ib.id);await screenshot('06-instances');
  await page.goto(config.url+'/releases?softwareId='+a.id+'&channel=Formal&releaseId='+fixture.release.id);await page.getByRole('heading',{name:/1\.0\.0/}).waitFor();await screenshot('07-releases');
  await page.goto(config.url+'/deployments?softwareId='+a.id);await page.getByRole('button',{name:fixture.deployment.id,exact:true}).click();await page.getByRole('heading',{name:/投放详情/}).waitFor();await screenshot('08-deployments');
  assert.deepEqual(await page.evaluate(()=>[localStorage.length,sessionStorage.length]),[0,0]);await complete(phase);
  await writeFile(join(results,'result.json'),JSON.stringify({passed:true,stages,injections,screenshots,displayOnly,releaseCommit:identity.releaseCommit,releaseDirty:identity.releaseDirty,
    verificationHost:process.platform+'/'+process.arch,runtimePlatform:identity.platform,simulatedInstallation:true,realGitLab:false,browserTrust:'demo leaf SPKI; API CA and hostname validated'},null,2)+'\n');
} catch(error) {
  await writeFile(join(results,'result.json'),JSON.stringify({passed:false,phase,stages,injections,errorType:error.name,locations:(error.stack??'').split('\n').filter(line=>line.includes('verify-audit.mjs:'))},null,2)+'\n');
  console.error('Audit verification stopped at '+phase+' ('+error.name+'); inspect the owned fixture before retrying writes.');process.exitCode=1;
} finally {agent.destroy();await browser?.close();}
