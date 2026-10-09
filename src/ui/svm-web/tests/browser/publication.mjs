import { readFile, mkdir } from 'node:fs/promises'
import { randomBytes, randomUUID, createHash } from 'node:crypto'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

const config = JSON.parse(await readFile(process.env.SVM_PUBLICATION_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, page, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1100 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  page = await context.newPage(); page.setDefaultTimeout(15000); const errors = []; page.on('pageerror', () => errors.push('pageerror'))
  const get = async path => { const r = await context.request.get(config.url + path); assert.equal(r.status(), 200); return r.json() }
  const write = async (path, data, method = 'POST') => { const session = await get('/api/v1/session'); const r = await context.request.fetch(config.url + '/api/v1/manage/' + path, { method, data, headers: { 'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.ok([200, 201].includes(r.status())); return r.json() }
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
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, ...['release.upload', 'release.publish', 'release.disable', 'audit.read', 'enrollment.manage'].map(operation => ({ softwareId: software.id, operation }))], expectedRevision: user.revision, reason: '夹具显式发布职责' }, 'PUT')
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
  const machine = async (path, bearer, data, key) => { const r = await context.request.post(config.url + path, { data, headers: { Authorization: 'Bearer ' + bearer, ...(key ? { 'Idempotency-Key': key } : {}) } }); assert.ok([200, 201].includes(r.status())); return r.json() }
  const registration = await machine('/api/v1/enrollment/instances', grant.id + '.' + grantSecret, { softwareId: software.id, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret }, randomUUID()); const bearer = registration.credentialId + '.' + secret
  const client = async path => { const r = await context.request.get(config.url + path, { headers: { Authorization: 'Bearer ' + bearer } }); assert.equal(r.status(), 200); return r.json() }
  assert.equal((await client('/api/v1/client/versions')).items.length, 0)
  const stream = await machine('/api/v1/client/report-streams', bearer, { expectedEpoch: 0 }, randomUUID())
  await machine('/api/v1/client/status-reports', bearer, { streamEpoch: stream.streamEpoch, reportSeq: 1, reportedAt: new Date().toISOString(), installationState: 'Installed', installedReleaseId: release.id, installedVersion: release.version, installedAt: null, runningState: 'Stopped', reportedIps: ['192.0.2.39'], databaseState: { mode: 'None', items: [] } })
  await page.getByRole('button', { name: '1.0.0', exact: true }).click(); const proof = (await get('/api/v1/manage/releases/' + release.id + '/test-evidence')).items[0]
  phase = 'select installed evidence and enter conclusion'; await page.locator('.publication-panel select').selectOption(proof.id); await page.getByLabel('测试通过原因').fill('现场安装验证通过'); await page.getByLabel('测试结论').fill('安装成功，停止运行时完成检查。\n允许正式使用。')
  phase = 'publish response loss and explicit original-key verification'
  const attempts = []; await page.route('**/api/v1/manage/releases/*/publish', async route => { attempts.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() }); const response = await route.fetch(); assert.equal(response.status(), 200); if (attempts.length === 1) await route.abort('failed'); else await route.fulfill({ response }) })
  await page.getByRole('button', { name: '转为正式版', exact: true }).click(); await expect(page.getByRole('button', { name: '核实原发布操作', exact: true })).toBeVisible(); assert.equal(attempts.length, 1); await expect(page.getByLabel('测试结论')).toBeDisabled()
  await page.getByRole('button', { name: '核实原发布操作', exact: true }).click(); await expect(page.getByRole('heading', { name: '正式发布记录', exact: true })).toBeVisible(); assert.equal(attempts.length, 2); assert.deepEqual(attempts[0], attempts[1]); await page.unroute('**/api/v1/manage/releases/*/publish')
  await expect(page.getByRole('button', { name: '正式版本', exact: true })).toHaveAttribute('aria-pressed', 'true'); await expect(page.locator('.publication-history')).toContainText(config.employeeNo)
  const formal = await get('/api/v1/manage/releases/' + release.id); assert.equal(formal.version, release.version); assert.equal(formal.packageId, release.packageId); assert.equal(formal.testEvidenceId, proof.id); assert.equal(formal.publishedBy, session.subjectId)
  phase = 'formal download and latest availability surfaces'
  const downloading = page.waitForEvent('download'); await page.getByRole('link', { name: '下载安装包', exact: true }).click(); const download = await downloading; assert.equal(createHash('sha256').update(await readFile(await download.path())).digest('hex'), hash)
  assert.equal((await client('/api/v1/client/versions')).items[0].id, release.id); assert.equal((await client('/api/v1/client/context')).latestAvailableFormalReleaseId, release.id)
  assert.equal((await get('/api/v1/manage/software/' + software.id)).latestAvailableFormalReleaseId, release.id)
  const inventory = (await get('/api/v1/manage/devices/' + device.id + '/software-inventory')).items[0]; assert.equal(inventory.instance.latestAvailableFormalReleaseId, release.id); assert.equal(inventory.instance.lastSnapshot.installedReleaseId, release.id)
  await page.screenshot({ path: config.artifacts + '/formal-publication.png', fullPage: true })
  phase = 'disable retains formal history and clears latest availability'
  await page.getByLabel('操作原因', { exact: true }).fill('验证正式发布历史保留'); await page.getByRole('button', { name: '停用版本', exact: true }).click(); await expect(page.locator('.detail .badge')).toHaveText('已停用'); await expect(page.getByRole('heading', { name: '正式发布记录', exact: true })).toBeVisible()
  assert.equal((await get(`/api/v1/manage/software/${software.id}/releases?channel=Formal`)).items[0].state, 'Disabled'); assert.equal((await get(`/api/v1/manage/software/${software.id}/releases?channel=Test`)).items.length, 0)
  assert.equal((await client('/api/v1/client/versions')).items.length, 0); assert.equal((await client('/api/v1/client/context')).latestAvailableFormalReleaseId, null)
  const denied = await context.request.get(config.url + '/api/v1/packages/' + release.packageId + '/content'); assert.ok([401, 403].includes(denied.status()))
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]); assert.equal(errors.length, 0)
  await page.screenshot({ path: config.artifacts + '/disabled-formal-history.png', fullPage: true })
  console.log('Browser publication verification passed')
} catch (error) { await page?.screenshot({ path: config.artifacts + '/failure.png', fullPage: true }).catch(() => {}); console.error('Browser publication verification failed at: ' + phase + ' (' + error.name + ')'); process.exitCode = 1 } finally { await browser?.close() }
