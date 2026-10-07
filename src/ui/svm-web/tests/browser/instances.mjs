import { readFile, mkdir } from 'node:fs/promises'
import { randomBytes, randomUUID } from 'node:crypto'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

const config = JSON.parse(await readFile(process.env.SVM_INSTANCE_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1000 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  const page = await context.newPage(); page.setDefaultTimeout(12000); const errors = []; page.on('pageerror', () => errors.push('pageerror'))
  phase = 'personnel login and first password change'
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo); await page.getByLabel('密码', { exact: true }).fill(config.initialPassword); await page.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('heading', { name: '首次登录，请修改密码' })).toBeVisible(); await expect(page.getByRole('navigation')).toHaveCount(0)
  await page.getByLabel('当前密码').fill(config.initialPassword); await page.getByLabel('新密码', { exact: true }).fill(config.password); await page.getByLabel('确认新密码').fill(config.password); await page.getByRole('button', { name: '修改密码', exact: true }).click(); await expect(page.getByRole('heading', { name: '软件目录', exact: true })).toBeVisible()
  // All data is explicitly owned by this disposable test factory. The APIs create it with real current authorization.
  const get = async path => { const r = await context.request.get(config.url + path); assert.equal(r.status(), 200); return r.json() }
  const write = async (path, data, method = 'POST') => { const session = await get('/api/v1/session'); const r = await context.request.fetch(config.url + '/api/v1/manage/' + path, { method, data, headers: { 'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID() } }); assert.ok([200, 201].includes(r.status())); return r.json() }
  phase = 'explicit site and enrollment authorization'
  const session = await get('/api/v1/session'); let user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, { softwareId: null, operation: 'asset.read' }, { softwareId: null, operation: 'asset.manage' }], expectedRevision: user.revision, reason: '浏览器夹具台账职责' }, 'PUT')
  const process = await write('processes', { code: 'INSTANCE-BROWSER-P', name: '接入验证工序' })
  const device = await write('devices', { processId: process.id, deviceNo: 'INSTANCE-BROWSER-D', name: '接入验证设备' })
  const software = await write('software', { code: 'INSTANCE-BROWSER-S', name: '接入验证视觉', category: 'Vision' })
  await write(`devices/${device.id}/software-bindings`, { softwareId: software.id, reason: '浏览器夹具映射' })
  user = await get('/api/v1/manage/users/' + session.subjectId)
  await write('subjects/' + user.id + '/permissions', { permissions: [...user.permissions, { softwareId: software.id, operation: 'enrollment.manage' }], expectedRevision: user.revision, reason: '显式分配接入职责' }, 'PUT')
  phase = 'grant creation and manual result verification'
  await page.goto(`${config.url}/enrollment?softwareId=${software.id}&deviceId=${device.id}`); await page.getByRole('button', { name: '签发登记许可', exact: true }).click()
  phase = 'grant form values'
  await page.getByLabel('有效期', { exact: true }).fill(new Date(Date.now() + 86400000).toISOString().slice(0, 16)); await page.getByLabel('操作原因', { exact: true }).fill('浏览器验证登记许可')
  const grantSecret = await page.getByLabel('登记许可秘密', { exact: true }).inputValue(); let dropped = false; const writes = []
  await page.route('**/api/v1/manage/enrollment-grants', async route => { if (route.request().method() !== 'POST') return route.continue(); writes.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() }); if (!dropped) { dropped = true; assert.equal((await route.fetch()).status(), 201); await route.abort('failed') } else await route.continue() })
  phase = 'grant response loss'
  await page.getByRole('button', { name: '创建许可', exact: true }).click(); await expect(page.getByRole('button', { name: '核实原操作' })).toBeVisible(); await expect(page.getByLabel('登记许可秘密', { exact: true })).toBeDisabled()
  phase = 'grant manual replay'
  assert.equal(writes.length, 1); await page.getByRole('button', { name: '核实原操作', exact: true }).click(); await expect(page.getByRole('button', { name: '完成并清除秘密' })).toBeVisible(); assert.equal(writes.length, 2); assert.deepEqual(writes[0], writes[1]); await page.unroute('**/api/v1/manage/enrollment-grants')
  const grants = await get('/api/v1/manage/enrollment-grants?softwareId=' + software.id); assert.equal(grants.items.length, 1); const grant = grants.items[0]
  await page.getByRole('button', { name: '完成并清除秘密', exact: true }).click(); assert.equal(await page.locator('input[type=password]').count(), 0)
  phase = 'external instance registration and real state report'
  const secret = randomBytes(32).toString('base64url')
  const machine = async (path, bearer, data, key) => { const r = await context.request.post(config.url + path, { data, headers: { Authorization: 'Bearer ' + bearer, ...(key ? { 'Idempotency-Key': key } : {}) } }); assert.ok([200, 201].includes(r.status())); return r.json() }
  const registration = await machine('/api/v1/enrollment/instances', grant.id + '.' + grantSecret, { softwareId: software.id, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret }, randomUUID())
  const navigateSite = async () => { await page.goto(config.url + '/site'); await page.getByRole('button', { name: '接入验证工序 INSTANCE-BROWSER-P' }).click(); await page.getByRole('button', { name: '接入验证设备 INSTANCE-BROWSER-D' }).click(); await expect(page.locator('.inventory-card')).toHaveCount(1) }
  await navigateSite(); await expect(page.locator('.inventory-card .badge')).toHaveText('尚未上报')
  const bearer = registration.credentialId + '.' + secret; const stream = await machine('/api/v1/client/report-streams', bearer, { expectedEpoch: 0 }, randomUUID())
  const report = { streamEpoch: stream.streamEpoch, reportSeq: 1, reportedAt: new Date().toISOString(), installationState: 'Installed', installedReleaseId: null, installedVersion: '4.5.6', installedAt: null, runningState: 'Running', reportedIps: ['192.0.2.8'], databaseState: { mode: 'None', items: [] } }
  await machine('/api/v1/client/status-reports', bearer, report); await page.getByRole('button', { name: '筛选软件', exact: true }).click(); await expect(page.locator('.inventory-card .badge')).toHaveText('正常上报'); await expect(page.locator('.inventory-card')).toContainText('4.5.6'); await expect(page.locator('.inventory-card')).toContainText('192.0.2.8')
  await page.screenshot({ path: config.artifacts + '/instance-site.png', fullPage: true })
  phase = 'installation history and access suspension'
  await page.getByRole('link', { name: '实例详情与安装履历', exact: true }).click(); await expect(page.getByRole('heading', { name: '设备安装履历', exact: true })).toBeVisible()
  await expect(page.locator('.permission-list').first()).toContainText('4.5.6'); await page.getByRole('button', { name: '暂停 API 接入', exact: true }).click(); await page.getByLabel('操作原因', { exact: true }).fill('验证仅暂停接入'); await page.getByRole('button', { name: '确认提交', exact: true }).click(); await expect(page.getByRole('button', { name: '启用 API 接入', exact: true })).toBeVisible()
  const denied = await context.request.get(config.url + '/api/v1/client/context', { headers: { Authorization: 'Bearer ' + bearer } }); assert.equal(denied.status(), 403); assert.equal((await denied.json()).code, 'INSTANCE_SUSPENDED')
  await page.getByRole('button', { name: '启用 API 接入', exact: true }).click(); await page.getByLabel('操作原因', { exact: true }).fill('恢复验证接入'); await page.getByRole('button', { name: '确认提交', exact: true }).click(); await expect(page.getByRole('button', { name: '暂停 API 接入', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '暂停 API 接入', exact: true })).toBeEnabled(); await expect(page.locator('.permission-list').first()).toContainText('4.5.6')
  await page.screenshot({ path: config.artifacts + '/instance-detail.png', fullPage: true }); assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]); assert.equal(errors.length, 0)
  console.log('Browser instance verification passed')
} catch (error) { console.error('Browser instance verification failed at: ' + phase + ' (' + error.name + ')'); process.exitCode = 1 } finally { await browser?.close() }
