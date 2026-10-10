import { readFile, mkdir, writeFile, access } from 'node:fs/promises'
import { randomBytes, randomUUID, createHash } from 'node:crypto'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

const config = JSON.parse(await readFile(process.env.SVM_DEPLOYMENT_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, page, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1100 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  page = await context.newPage(); page.setDefaultTimeout(15000); const errors = []; page.on('pageerror', () => errors.push('pageerror'))
  const get = async path => { const r = await context.request.get(config.url + path); assert.equal(r.status(), 200); return r.json() }
  const write = async (path, data, method = 'POST') => { const session = await get('/api/v1/session'); const r = await context.request.fetch(config.url + '/api/v1/manage/' + path, { method, data, headers: { 'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.ok([200, 201, 202].includes(r.status()), 'status '+r.status()); return r.json() }
  phase = 'login and first password restriction'
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo); await page.getByLabel('密码', { exact: true }).fill(config.initialPassword); await page.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('heading', { name: '首次登录，请修改密码' })).toBeVisible()
  const initial = await get('/api/v1/session'); const restricted = await context.request.post(config.url + '/api/v1/manage/releases/' + randomUUID() + '/publish', { data: { testEvidenceId: randomUUID(), publishReason: 'fixture', publishConclusion: 'fixture', expectedRevision: 1 }, headers: { 'X-CSRF-TOKEN': initial.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.equal(restricted.status(), 403)
  await page.getByLabel('当前密码').fill(config.initialPassword); await page.getByLabel('新密码', { exact: true }).fill(config.password); await page.getByLabel('确认新密码').fill(config.password); await page.getByRole('button', { name: '修改密码', exact: true }).click(); await expect(page.getByRole('heading', { name: '软件目录', exact: true })).toBeVisible()
  phase = 'explicit disposable catalog and publication permission'
  const session = await get('/api/v1/session'); let user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, { softwareId: null, operation: 'asset.read' }, { softwareId: null, operation: 'asset.manage' }], expectedRevision: user.revision, reason: '发布浏览器夹具台账授权' }, 'PUT')
  const process = await write('processes', { code: 'PUB-BROWSER-P', name: '发布验证工序' }); const device = await write('devices', { processId: process.id, deviceNo: 'PUB-BROWSER-D', name: '发布验证设备' }); const software = await write('software', { code: 'PUB-BROWSER-S', name: '发布验证视觉', category: 'Vision' })
  await write(`devices/${device.id}/software-bindings`, { softwareId: software.id, reason: '发布浏览器夹具映射' }); user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, ...['release.upload', 'release.publish', 'release.disable', 'audit.read', 'enrollment.manage', 'deployment.create', 'deployment.control', 'task.closeUnknown'].map(operation => ({ softwareId: software.id, operation }))], expectedRevision: user.revision, reason: '夹具显式发布职责' }, 'PUT')
  phase = 'site entry and test package'
  await page.goto(config.url + '/site'); await page.getByRole('button', { name: '发布验证工序 PUB-BROWSER-P' }).click(); await page.getByRole('button', { name: '发布验证设备 PUB-BROWSER-D' }).click(); await page.getByRole('link', { name: '版本与安装包', exact: true }).click()
  await page.getByRole('button', { name: '登记版本并上传', exact: true }).click(); await page.getByLabel('更新内容').fill('真实 HTTPS 发布验证'); await page.getByLabel('变更原因').fill('一次性发布验证')
  const bytes = randomBytes(1048617), hash = createHash('sha256').update(bytes).digest('hex')
  await page.getByLabel('安装包', { exact: true }).setInputFiles({ name: 'publication-fixture.zip', mimeType: 'application/zip', buffer: bytes }); await expect(page.getByText('SHA-256：' + hash, { exact: true })).toBeVisible(); await page.getByRole('button', { name: '登记并上传', exact: true }).click()
  await expect(page.getByRole('link', { name: '下载安装包', exact: true })).toBeVisible({ timeout: 45000 }); await expect(page.locator('.detail')).toContainText('2 / 2')
  await expect(page.getByRole('button', { name: '转为正式版', exact: true })).toBeDisabled(); await expect(page.locator('.publication-panel')).toContainText('尚无可选择的安装证据')
  const release = (await get(`/api/v1/manage/software/${software.id}/releases?channel=Test`)).items[0]
  const path = '/api/v1/manage/releases/' + release.id + '/publish'; const data = { testEvidenceId: randomUUID(), publishReason: 'fixture', publishConclusion: 'fixture', expectedRevision: release.revision }
  phase = 'missing CSRF is rejected'
  assert.equal((await context.request.post(config.url + path, { data, headers: { 'Idempotency-Key': randomUUID() } })).status(), 403)
  phase = 'unknown publication fields are rejected'; const current = await get('/api/v1/session'); const unknown = await context.request.post(config.url + path, { data: { ...data, publishedBy: randomUUID() }, headers: { 'X-CSRF-TOKEN': current.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.equal(unknown.status(), 400); assert.equal((await unknown.json()).code, 'UNKNOWN_FIELD')
  phase = 'anonymous publication is rejected'; const anonymous = await browser.newContext({ ignoreHTTPSErrors: true }); const anonymousSession = await (await anonymous.request.get(config.url + '/api/v1/session')).json(); assert.equal((await anonymous.request.post(config.url + path, { data, headers: { 'X-CSRF-TOKEN': anonymousSession.csrfToken, 'Idempotency-Key': randomUUID() } })).status(), 401); await anonymous.close()
  phase = 'installed stopped evidence and default formal empty query'
  const grantSecret = randomBytes(32).toString('base64url'), secret = randomBytes(32).toString('base64url')
  const grant = await write('enrollment-grants', { softwareId: software.id, deviceIds: [device.id], expiresAt: new Date(Date.now() + 3600000).toISOString(), maxInstances: 1, secretMaterial: grantSecret, reason: '发布安装证据' })
  const machine = async (path, bearer, data, key) => { const r = await context.request.post(config.url + path, { data, headers: { Authorization: 'Bearer ' + bearer, ...(key ? { 'Idempotency-Key': key } : {}) } }); assert.ok([200, 201, 202].includes(r.status()), 'status '+r.status()); return r.json() }
  const registration = await machine('/api/v1/enrollment/instances', grant.id + '.' + grantSecret, { softwareId: software.id, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret }, randomUUID()); const bearer = registration.credentialId + '.' + secret
  const client = async path => { const r = await context.request.get(config.url + path, { headers: { Authorization: 'Bearer ' + bearer } }); assert.equal(r.status(), 200); return r.json() }
  assert.equal((await client('/api/v1/client/versions')).items.length, 0)
  const stream = await machine('/api/v1/client/report-streams', bearer, { expectedEpoch: 0 }, randomUUID())
  await machine('/api/v1/client/status-reports', bearer, { streamEpoch: stream.streamEpoch, reportSeq: 1, reportedAt: new Date().toISOString(), installationState: 'Installed', installedReleaseId: release.id, installedVersion: release.version, installedAt: null, runningState: 'Stopped', reportedIps: ['192.0.2.39'], databaseState: { mode: 'None', items: [] } })
  await page.getByRole('button', { name: '1.0.0', exact: true }).click(); const proof = (await get('/api/v1/manage/releases/' + release.id + '/test-evidence')).items[0]
  phase = 'select installed evidence and enter conclusion'; await page.locator('.publication-panel select').selectOption(proof.id); await page.getByLabel('测试通过原因').fill('现场安装验证通过'); await page.getByLabel('测试结论').fill('安装成功，停止运行时完成检查。\n允许正式使用。')
  await page.getByRole('button', { name: '转为正式版', exact: true }).click(); await expect(page.getByRole('heading', { name: '正式发布记录', exact: true })).toBeVisible()
  const checkpoint = async (name, data = {}) => { await writeFile(config.coordination + '/' + name + '.json', JSON.stringify(data)); const until=Date.now()+120000; while(Date.now()<until) { try { await access(config.coordination+'/'+name+'.go'); return } catch {} await new Promise(r=>setTimeout(r,100)) } throw new Error('checkpoint '+name) }
  const targetDevice=await write('devices',{processId:process.id,deviceNo:'UPDATE-BROWSER-TARGET',name:'更新验证目标设备'})
  await write(`devices/${targetDevice.id}/software-bindings`,{softwareId:software.id,reason:'夹具目标映射'})
  const gs=randomBytes(32).toString('base64url'), targetSecret=randomBytes(32).toString('base64url')
  const targetGrant=await write('enrollment-grants',{softwareId:software.id,deviceIds:[targetDevice.id],expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:gs,reason:'更新流程夹具'})
  const target=await machine('/api/v1/enrollment/instances',targetGrant.id+'.'+gs,{softwareId:software.id,deviceId:targetDevice.id,installationKey:randomUUID(),secretMaterial:targetSecret},randomUUID())
  const targetBearer=target.credentialId+'.'+targetSecret
  const targetStream=await machine('/api/v1/client/report-streams',targetBearer,{expectedEpoch:0},randomUUID())
  const report=seq=>({streamEpoch:targetStream.streamEpoch,reportSeq:seq,reportedAt:new Date().toISOString(),installationState:'NotInstalled',installedReleaseId:null,installedVersion:null,installedAt:null,runningState:'Stopped',reportedIps:['192.0.2.40'],databaseState:{mode:'None',items:[]}})
  await machine('/api/v1/client/status-reports',targetBearer,report(1))
  const targetGet=async path=>{const r=await context.request.get(config.url+path,{headers:{Authorization:'Bearer '+targetBearer}});assert.equal(r.status(),200);return r.json()}
  phase='offline workers retain preparation intent'
  await checkpoint('offline-preparation')
  await page.goto(config.url+`/deployments?softwareId=${software.id}&releaseId=${release.id}`)
  await page.getByRole('button',{name:'选择更新目标',exact:true}).click()
  await page.getByRole('checkbox',{name:'选择实例 '+target.instanceId,exact:true}).check()
  await page.getByRole('checkbox',{name:'使用配置的下一时段',exact:true}).uncheck()
  const localInput=offset=>new Date(Date.now()+offset).toISOString().slice(0,16)
  await page.getByLabel('UTC 开始',{exact:true}).fill(localInput(-60000));await page.getByLabel('UTC 最晚开始',{exact:true}).fill(localInput(3600000));await page.getByLabel('投放原因',{exact:true}).fill('真实 HTTPS 任务更新夹具')
  const attempts=[];let original
  await page.route('**/api/v1/manage/deployments',async route=>{attempts.push({key:route.request().headers()['idempotency-key'],body:route.request().postData()});const response=await route.fetch();assert.equal(response.status(),202);original=await response.json();if(attempts.length===1)await route.abort('failed');else await route.fulfill({response})})
  await page.getByRole('button',{name:'封存目标并创建更新投放',exact:true}).click();await expect(page.getByRole('button',{name:'核实原操作',exact:true})).toBeVisible();assert.equal(attempts.length,1)
  await page.getByRole('button',{name:'核实原操作',exact:true}).click();await expect(page.getByRole('heading',{name:/投放详情/})).toBeVisible();assert.equal(attempts.length,2);assert.deepEqual(attempts[0],attempts[1]);await page.unroute('**/api/v1/manage/deployments')
  const pausedBeforeDispatch=await write('deployments/'+original.id+'/pause',{expectedRevision:original.revision,reason:'夹具验证旧派发代次'})
  await write('deployments/'+original.id+'/resume',{expectedRevision:pausedBeforeDispatch.revision,reason:'夹具恢复原准备工作'})
  await checkpoint('created-preparation',{deploymentId:original.id,workId:original.workId,softwareId:software.id})
  const waitTasks=async dep=>{await expect.poll(async()=>{const x=await get('/api/v1/manage/deployments/'+dep+'/tasks');return x.items[0]?.state},{timeout:60000}).toBe('Available');return (await get('/api/v1/manage/deployments/'+dep+'/tasks')).items}
  const task=(await waitTasks(original.id))[0];assert.equal(task.instanceId,target.instanceId)
  await checkpoint('prepared',{deploymentId:original.id,workId:original.workId,softwareId:software.id})
  phase='client ownership preflight stable claim and sequenced result'
  const foreign=await context.request.get(config.url+'/api/v1/client/tasks/'+task.id,{headers:{Authorization:'Bearer '+bearer}});assert.ok([403,404].includes(foreign.status()))
  const claimKey=randomUUID(), claimed=await machine('/api/v1/client/tasks/'+task.id+'/claim',targetBearer,{},claimKey);assert.equal(claimed.task.id,task.id);assert.equal(claimed.package.id,release.packageId)
  assert.equal((await machine('/api/v1/client/tasks/'+task.id+'/claim',targetBearer,{},claimKey)).attemptId,claimed.attemptId)
  const attached=report(2), preflight={downloadedSha256:hash,dataProtectionConfirmed:false}
  const denied=await context.request.post(config.url+'/api/v1/client/attempts/'+claimed.attemptId+'/start',{data:{stateReport:attached,preflight},headers:{Authorization:'Bearer '+targetBearer,'Idempotency-Key':randomUUID()}});assert.equal(denied.status(),422);assert.equal((await denied.json()).code,'VALIDATION_FAILED')
  assert.equal((await targetGet('/api/v1/client/context')).lastReportSeq,1)
  const startKey=randomUUID(),startBody={stateReport:attached,preflight:{...preflight,dataProtectionConfirmed:true}}
  const permit=await machine('/api/v1/client/attempts/'+claimed.attemptId+'/start',targetBearer,startBody,startKey);assert.equal(permit.taskId,task.id);assert.equal((await machine('/api/v1/client/attempts/'+claimed.attemptId+'/start',targetBearer,startBody,startKey)).startAuthorizedAt,permit.startAuthorizedAt)
  const terminal={eventId:randomUUID(),sequence:1,kind:'Terminal',result:'Succeeded'}
  const receipt=await machine('/api/v1/client/attempts/'+claimed.attemptId+'/receipts',targetBearer,terminal);assert.equal(receipt.taskState,'Succeeded');assert.equal((await machine('/api/v1/client/attempts/'+claimed.attemptId+'/receipts',targetBearer,terminal)).receiptId,receipt.receiptId)
  const latest=await get('/api/v1/manage/instances/'+target.instanceId);assert.equal(latest.lastSnapshot.installationState,'NotInstalled');assert.equal(latest.latestTaskId,task.id);assert.equal(latest.latestTaskResult,'Succeeded')
  const inventory=(await get(`/api/v1/manage/devices/${targetDevice.id}/software-inventory`)).items[0];assert.equal(inventory.instance.latestTaskId,task.id)
  await page.getByRole('button',{name:'刷新当前投放',exact:true}).click();await expect(page.locator('body')).toContainText('Succeeded');await page.screenshot({path:config.artifacts+'/update-result.png',fullPage:true})
  phase='retained integration materials'
  await page.getByLabel('数据位置（每行一项）',{exact:true}).fill('/fixture/data');await page.getByLabel('升级行为',{exact:true}).fill('接入方保护数据后安装');await page.getByLabel('回退行为',{exact:true}).fill('本批不提供回退');await page.getByLabel('恢复说明',{exact:true}).fill('接入方现场核实');await page.getByLabel('登记原因',{exact:true}).fill('一次性验证');await page.getByRole('button',{name:'登记新资料修订',exact:true}).click();await expect(page.getByRole('heading',{name:/修订 1/})).toBeVisible()
  phase='cancel control survives worker restart'
  const selection2=await write('target-selections',{softwareId:software.id,mode:'Explicit'});const chunk=await write(`target-selections/${selection2.id}/chunks/0`,{instanceIds:[target.instanceId]},'PUT');const sealed=await write(`target-selections/${selection2.id}/seal`,{expectedRevision:chunk.revision,chunkCount:1,expectedDistinctCount:1})
  const second=await write('deployments',{softwareId:software.id,selectionId:sealed.id,kind:'Update',targetReleaseId:release.id,window:{notBefore:new Date(Date.now()-60000).toISOString(),latestStart:new Date(Date.now()+3600000).toISOString()},reason:'控制恢复夹具',retryOfDeploymentId:original.id});await waitTasks(second.id)
  await page.getByRole('button',{name:'刷新列表',exact:true}).click();await page.getByRole('button',{name:second.id,exact:true}).click();await checkpoint('offline-control')
  await page.getByLabel('控制原因',{exact:true}).fill('取消夹具未获许可任务');const controlResponse=page.waitForResponse(r=>r.url().endsWith('/deployments/'+second.id+'/cancel') && r.request().method()==='POST');await page.getByRole('button',{name:'取消未获许可任务',exact:true}).click();const response=await controlResponse;assert.equal(response.status(),202);const control=await response.json();assert.equal(control.id,control.workId);assert.equal(control.kind,'Cancel');assert.equal(response.headers().location,'/api/v1/manage/deployment-work/'+control.id);assert.equal(response.headers()['retry-after'],'5')
  await checkpoint('created-control',{deploymentId:second.id,workId:control.workId,softwareId:software.id})
  await expect.poll(async()=>(await get('/api/v1/manage/deployment-work/'+control.workId)).state,{timeout:60000}).toBe('Completed')
  const items=(await get('/api/v1/manage/deployment-work/'+control.workId+'/items')).items;assert.equal(items.length,1);assert.equal(items[0].outcome,'Applied')
  assert.equal((await get('/api/v1/manage/deployments/'+second.id+'/tasks')).items[0].state,'Canceled');assert.ok((await get('/api/v1/manage/deployments/'+second.id+'/batches')).items.every(b=>b.state==='Closed'))
  await checkpoint('control-finished',{deploymentId:second.id,workId:control.workId,softwareId:software.id})
  phase='filter selection uses persisted snapshot'
  const filtered=await write('target-selections',{softwareId:software.id,mode:'Filter',filter:{softwareId:software.id,deviceId:targetDevice.id}})
  await expect.poll(async()=>(await get('/api/v1/manage/target-selections/'+filtered.id)).state,{timeout:60000}).toBe('Sealed');assert.equal((await get('/api/v1/manage/target-selections/'+filtered.id)).memberCount,1)
  await page.getByRole('button',{name:'刷新当前投放',exact:true}).click();await page.screenshot({path:config.artifacts+'/control-recovered.png',fullPage:true})
  assert.deepEqual(await page.evaluate(()=>[localStorage.length,sessionStorage.length]),[0,0]);assert.equal(errors.length,0);console.log('Browser deployment verification passed')
} catch(error) {await page?.screenshot({path:config.artifacts+'/failure.png',fullPage:true}).catch(()=>{});console.error('Browser deployment verification failed at: '+phase+' ('+error.name+': '+error.message+')');process.exitCode=1}finally{await browser?.close()}
