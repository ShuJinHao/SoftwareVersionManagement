import { readFile, mkdir } from 'node:fs/promises'
import { randomUUID, randomBytes, createHash } from 'node:crypto'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

const config = JSON.parse(await readFile(process.env.SVM_GITLAB_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, page, phase = 'login'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1050 }, locale: 'zh-CN' })
  page = await context.newPage(); page.setDefaultTimeout(15000)
  const get = async path => { const r = await context.request.get(config.url + path); assert.equal(r.status(), 200); return r.json() }
  const write = async (path, data, method = 'POST') => { const s = await get('/api/v1/session'); const r = await context.request.fetch(config.url + '/api/v1/manage/' + path, { method, data, headers: { 'X-CSRF-TOKEN': s.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.ok([200, 201, 202].includes(r.status()), 'management status ' + r.status()); return r.json() }
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo); await page.getByLabel('密码', { exact: true }).fill(config.password); await page.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('heading', { name: '现场台账', exact: true })).toBeVisible()
  phase = 'GitLab formal entry and original sync key'
  await page.goto(config.url + '/releases?softwareId=' + config.softwareId)
  await expect(page.getByRole('heading', { name: 'GitLab 正式版本', exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: '登记版本并上传', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '正式版本', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByLabel('历史 Tag（留空扫描正式发布）').fill('v2.0.0')
  const attempts = []; let resolveVerification
  const verifiedResponse = new Promise(resolve => { resolveVerification = resolve })
  await page.route('**/release-sync', async route => { attempts.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() }); const index = attempts.length; const response = await route.fetch(); assert.equal(response.status(), 202); if (index === 1) await route.abort('failed'); else { await route.fulfill({ response }); resolveVerification() } })
  await page.getByRole('button', { name: '立即同步', exact: true }).click(); await expect(page.getByRole('button', { name: '核实原同步操作', exact: true })).toBeVisible(); assert.equal(attempts.length, 1)
  await page.getByRole('button', { name: '核实原同步操作', exact: true }).click(); await verifiedResponse; await expect(page.getByRole('button', { name: '立即同步', exact: true })).toBeEnabled(); assert.equal(attempts.length, 2); assert.deepEqual(attempts[0], attempts[1]); await page.unroute('**/release-sync')
  await page.getByRole('button', { name: '2.0.0', exact: true }).click(); await expect(page.getByRole('heading', { name: 'GitLab 发布来源', exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: '转为正式版', exact: true })).toHaveCount(0)
  const release = await get('/api/v1/manage/releases/' + config.releaseId); assert.equal(release.state, 'Formal'); assert.equal(release.testEvidenceId, null)
  phase = 'authorized package download'
  const downloaded = await context.request.get(config.url + '/api/v1/packages/' + release.packageId + '/content'); assert.equal(downloaded.status(), 200); assert.equal(createHash('sha256').update(await downloaded.body()).digest('hex'), config.sha256)
  await page.screenshot({ path: config.artifacts + '/gitlab-formal.png', fullPage: true })
  phase = 'explicit instance update'
  const process = await write('processes', { code: 'GITLAB-FIXTURE-P', name: 'GitLab 验证工序' })
  const device = await write('devices', { processId: process.id, deviceNo: 'GITLAB-FIXTURE-D', name: 'GitLab 验证设备' })
  await write('devices/' + device.id + '/software-bindings', { softwareId: config.softwareId, reason: '夹具映射' })
  const gs = randomBytes(32).toString('base64url'), secret = randomBytes(32).toString('base64url')
  const grant = await write('enrollment-grants', { softwareId: config.softwareId, deviceIds: [device.id], expiresAt: new Date(Date.now() + 3600000).toISOString(), maxInstances: 1, secretMaterial: gs, reason: '夹具接入' })
  const machine = async (path, bearer, data, key) => { const r = await context.request.post(config.url + path, { data, headers: { Authorization: 'Bearer ' + bearer, ...(key ? { 'Idempotency-Key': key } : {}) } }); assert.ok([200, 201, 202].includes(r.status()), 'machine status ' + r.status()); return r.json() }
  const instance = await machine('/api/v1/enrollment/instances', grant.id + '.' + gs, { softwareId: config.softwareId, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret }, randomUUID())
  const bearer = instance.credentialId + '.' + secret, stream = await machine('/api/v1/client/report-streams', bearer, { expectedEpoch: 0 }, randomUUID())
  const clientGet = async path => { const r = await context.request.get(config.url + path, { headers: { Authorization: 'Bearer ' + bearer } }); assert.equal(r.status(), 200); return r.json() }
  const report = sequence => ({ streamEpoch: stream.streamEpoch, reportSeq: sequence, reportedAt: new Date().toISOString(), installationState: 'NotInstalled', installedReleaseId: null, installedVersion: null, installedAt: null, runningState: 'Stopped', reportedIps: ['192.0.2.45'], databaseState: { mode: 'None', items: [] } })
  await machine('/api/v1/client/status-reports', bearer, report(1)); assert.equal((await clientGet('/api/v1/client/versions')).items[0].id, release.id)
  assert.equal((await get('/api/v1/manage/deployments?softwareId=' + config.softwareId)).items.length, 0)
  const selection = await write('target-selections', { softwareId: config.softwareId, mode: 'Explicit' })
  const chunk = await write('target-selections/' + selection.id + '/chunks/0', { instanceIds: [instance.instanceId] }, 'PUT')
  const sealed = await write('target-selections/' + selection.id + '/seal', { expectedRevision: chunk.revision, chunkCount: 1, expectedDistinctCount: 1 })
  const deployment = await write('deployments', { softwareId: config.softwareId, selectionId: sealed.id, kind: 'Update', targetReleaseId: release.id, window: { notBefore: new Date(Date.now() - 60000).toISOString(), latestStart: new Date(Date.now() + 3600000).toISOString() }, reason: 'GitLab 版本显式现场投放' })
  await expect.poll(async () => (await get('/api/v1/manage/deployments/' + deployment.id + '/tasks')).items[0]?.state, { timeout: 60000 }).toBe('Available')
  const task = (await get('/api/v1/manage/deployments/' + deployment.id + '/tasks')).items[0]
  const claim = await machine('/api/v1/client/tasks/' + task.id + '/claim', bearer, {}, randomUUID())
  await machine('/api/v1/client/attempts/' + claim.attemptId + '/start', bearer, { stateReport: report(2), preflight: { downloadedSha256: config.sha256, dataProtectionConfirmed: true } }, randomUUID())
  await machine('/api/v1/client/attempts/' + claim.attemptId + '/receipts', bearer, { eventId: randomUUID(), sequence: 1, kind: 'Terminal', result: 'Succeeded' })
  assert.equal((await get('/api/v1/manage/instances/' + instance.instanceId)).lastSnapshot.installationState, 'NotInstalled')
  phase = 'installation facts follow report'
  await machine('/api/v1/client/status-reports', bearer, { ...report(3), installationState: 'Installed', installedReleaseId: release.id, installedVersion: release.version, runningState: 'Running' })
  assert.equal((await get('/api/v1/manage/instances/' + instance.instanceId)).lastSnapshot.installedReleaseId, release.id)
  await page.goto(config.url + '/deployments?softwareId=' + config.softwareId + '&releaseId=' + release.id); await page.getByRole('button', { name: deployment.id, exact: true }).click(); await expect(page.locator('body')).toContainText('Succeeded')
  await page.screenshot({ path: config.artifacts + '/gitlab-update-result.png', fullPage: true }); assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]); console.log('Browser GitLab verification passed')
} catch (error) { await page?.screenshot({ path: config.artifacts + '/failure.png', fullPage: true }).catch(() => {}); console.error('Browser GitLab verification failed at: ' + phase + ' (' + error.name + ': ' + error.message + ')'); process.exitCode = 1 }
finally { await browser?.close() }
