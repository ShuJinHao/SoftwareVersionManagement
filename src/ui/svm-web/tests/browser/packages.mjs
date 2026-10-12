import { readFile, mkdir } from 'node:fs/promises'
import { randomBytes, randomUUID, createHash } from 'node:crypto'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

const config = JSON.parse(await readFile(process.env.SVM_PACKAGE_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1050 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  const page = await context.newPage(); page.setDefaultTimeout(15000); const errors = []; page.on('pageerror', () => errors.push('pageerror'))
  phase = 'login and first password change'
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo); await page.getByLabel('密码', { exact: true }).fill(config.initialPassword); await page.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('heading', { name: '首次登录，请修改密码' })).toBeVisible()
  await page.getByLabel('当前密码').fill(config.initialPassword); await page.getByLabel('新密码', { exact: true }).fill(config.password); await page.getByLabel('确认新密码').fill(config.password); await page.getByRole('button', { name: '修改密码', exact: true }).click(); await expect(page.getByRole('heading', { name: '软件目录', exact: true })).toBeVisible()
  const get = async path => { const r = await context.request.get(config.url + path); assert.equal(r.status(), 200); return r.json() }
  const write = async (path, data, method = 'POST') => { const session = await get('/api/v1/session'); const r = await context.request.fetch(config.url + '/api/v1/manage/' + path, { method, data, headers: { 'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.ok([200, 201].includes(r.status())); return r.json() }
  phase = 'explicit disposable factory catalog and permissions'
  const session = await get('/api/v1/session'); let user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, { softwareId: null, operation: 'asset.read' }, { softwareId: null, operation: 'asset.manage' }], expectedRevision: user.revision, reason: '版本浏览器夹具台账授权' }, 'PUT')
  const process = await write('processes', { code: 'PKG-BROWSER-P', name: '包验证工序' }); const device = await write('devices', { processId: process.id, deviceNo: 'PKG-BROWSER-D', name: '包验证设备' }); const software = await write('software', { code: 'PKG-BROWSER-S', name: '包验证视觉', category: 'Vision' })
  await write(`devices/${device.id}/software-bindings`, { softwareId: software.id, reason: '版本浏览器夹具映射' }); user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, ...['release.upload', 'release.disable', 'audit.read', 'enrollment.manage'].map(operation => ({ softwareId: software.id, operation }))], expectedRevision: user.revision, reason: '显式版本包职责' }, 'PUT')
  phase = 'site software entry and real empty formal view'
  await page.goto(config.url + '/site'); await page.getByRole('button', { name: '包验证工序 PKG-BROWSER-P' }).click(); await page.getByRole('button', { name: '包验证设备 PKG-BROWSER-D' }).click(); await page.getByRole('link', { name: '测试版本与安装包', exact: true }).click()
  await page.getByRole('button', { name: '正式版本', exact: true }).click(); await expect(page.getByText('尚无正式版本。测试转正式将在后续批次开放。')).toBeVisible(); await page.getByRole('button', { name: '测试版本', exact: true }).click()
  phase = 'web worker chunked hash and original upload response loss'
  const bytes = randomBytes(3 * 1024 * 1024 + 41), hash = createHash('sha256').update(bytes).digest('hex')
  await page.getByRole('button', { name: '登记版本并上传', exact: true }).click(); await page.getByLabel('更新内容').fill('真实浏览器双副本验证'); await page.getByLabel('变更原因').fill('一次性功能验证')
  await page.getByLabel('安装包', { exact: true }).setInputFiles({ name: 'browser-fixture.zip', mimeType: 'application/zip', buffer: bytes }); await expect(page.getByText('SHA-256：' + hash, { exact: true })).toBeVisible()
  let writes = 0
  await page.route('**/api/v1/manage/uploads/*/content', async route => { if (route.request().method() !== 'PUT') return route.continue(); writes++; assert.equal((await route.fetch()).status(), 202); await route.abort('failed') })
  await page.getByRole('button', { name: '登记并上传', exact: true }).click(); await expect(page.getByRole('button', { name: '核实接收结果', exact: true })).toBeVisible(); assert.equal(writes, 1)
  await page.getByRole('button', { name: '核实接收结果', exact: true }).click(); await expect(page.getByText('已核实接收事实。', { exact: true })).toBeVisible(); assert.equal(writes, 1); await page.unroute('**/api/v1/manage/uploads/*/content')
  phase = 'consumer work progress and Nginx download'
  await expect(page.getByRole('link', { name: '下载安装包', exact: true })).toBeVisible({ timeout: 45000 }); await expect(page.locator('.detail')).toContainText('2 / 2')
  const releases = await get(`/api/v1/manage/software/${software.id}/releases?channel=Test`); assert.equal(releases.items.length, 1); const release = releases.items[0]; assert.equal(release.version, '1.0.0'); assert.equal(release.state, 'Test')
  const downloading = page.waitForEvent('download'); await page.getByRole('link', { name: '下载安装包', exact: true }).click(); const download = await downloading; assert.equal(createHash('sha256').update(await readFile(await download.path())).digest('hex'), hash)
  phase = 'real instance installation linked to release'
  const grantSecret = randomBytes(32).toString('base64url'), secret = randomBytes(32).toString('base64url')
  const grant = await write('enrollment-grants', { softwareId: software.id, deviceIds: [device.id], expiresAt: new Date(Date.now() + 3600000).toISOString(), maxInstances: 1, secretMaterial: grantSecret, reason: '浏览器安装证据' })
  const machine = async (path, bearer, data, key) => { const r = await context.request.post(config.url + path, { data, headers: { Authorization: 'Bearer ' + bearer, ...(key ? { 'Idempotency-Key': key } : {}) } }); assert.ok([200, 201].includes(r.status())); return r.json() }
  const registration = await machine('/api/v1/enrollment/instances', grant.id + '.' + grantSecret, { softwareId: software.id, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret }, randomUUID()); const bearer = registration.credentialId + '.' + secret
  const stream = await machine('/api/v1/client/report-streams', bearer, { expectedEpoch: 0 }, randomUUID())
  await machine('/api/v1/client/status-reports', bearer, { streamEpoch: stream.streamEpoch, reportSeq: 1, reportedAt: new Date().toISOString(), installationState: 'Installed', installedReleaseId: release.id, installedVersion: release.version, installedAt: null, runningState: 'Running', reportedIps: ['192.0.2.29'], databaseState: { mode: 'None', items: [] } })
  await page.getByRole('button', { name: '1.0.0', exact: true }).click(); await expect(page.locator('.detail .permission-list').first()).toContainText(registration.instanceId); await expect(page.locator('.detail')).toContainText('下载传输记录')
  await page.screenshot({ path: config.artifacts + '/release-package.png', fullPage: true })
  phase = 'disable rejects new downloads and keeps history'
  await page.getByLabel('操作原因', { exact: true }).fill('验证停止新下载'); await page.getByRole('button', { name: '停用版本', exact: true }).click(); await expect(page.locator('.detail .badge')).toHaveText('已停用')
  const denied = await context.request.get(config.url + '/api/v1/packages/' + release.packageId + '/content'); assert.ok([401, 403].includes(denied.status()))
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]); assert.equal(errors.length, 0)
  console.log('Browser package verification passed')
} catch (error) { console.error('Browser package verification failed at: ' + phase + ' (' + error.name + ')'); process.exitCode = 1 } finally { await browser?.close() }
