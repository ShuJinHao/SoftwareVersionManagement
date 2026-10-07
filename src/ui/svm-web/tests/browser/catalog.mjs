import { readFile, mkdir } from 'node:fs/promises'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

// Test fixture owns the explicit factory, random credentials, ephemeral database and HTTPS host.
const config = JSON.parse(await readFile(process.env.SVM_SITE_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, page, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1500, height: 1000 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  page = await context.newPage(); page.setDefaultTimeout(12000); const errors = []; page.on('pageerror', () => errors.push('pageerror'))
  const dialog = () => page.getByRole('dialog')
  const field = label => dialog().getByLabel(label, { exact: true })
  const save = async (label = '保存') => { await dialog().getByRole('button', { name: label, exact: true }).click(); await expect(dialog()).toHaveCount(0) }
  const reason = text => field('操作原因').fill(text)
  const nav = name => page.getByRole('navigation', { name: '管理导航' }).getByRole('link', { name, exact: true }).click()
  phase = 'authentication and first password change'
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo); await page.getByLabel('密码', { exact: true }).fill(config.initialPassword); await page.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('heading', { name: '首次登录，请修改密码' })).toBeVisible(); await expect(page.getByRole('navigation')).toHaveCount(0)
  await page.getByLabel('当前密码').fill(config.initialPassword); await page.getByLabel('新密码', { exact: true }).fill(config.password); await page.getByLabel('确认新密码').fill(config.password); await page.getByRole('button', { name: '修改密码', exact: true }).click()
  await expect(page.getByRole('heading', { name: '软件目录', exact: true })).toBeVisible()
  phase = 'explicit asset grants through the administration page'
  await nav('人员账号'); await page.getByRole('button', { name: '测试管理员', exact: true }).click(); await page.getByRole('button', { name: '维护厂级权限与软件授权' }).click()
  await dialog().getByLabel('查看现场台账').check(); await dialog().getByLabel('维护现场台账').check(); await reason('分配现场职责'); await save()
  await expect(page.getByRole('link', { name: '现场台账', exact: true })).toBeVisible()
  phase = 'software creation with a lost response'
  await nav('软件目录'); let drop = true; const writes = []
  await page.route('**/api/v1/manage/software', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    writes.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() })
    if (drop) { drop = false; assert.equal((await route.fetch()).status(), 201); await route.abort('failed') } else await route.continue()
  })
  await page.getByRole('button', { name: '登记软件', exact: true }).click(); await field('软件代码').fill('BROWSER-UPPER'); await field('软件名称').fill('验证上位机'); await field('软件分类').selectOption('UpperComputer'); await field('软件说明').fill('纯文本更新资料 <script> 不执行')
  await dialog().getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog().getByRole('button', { name: '核实原操作' })).toBeVisible()
  await page.waitForTimeout(500); assert.equal(writes.length, 1); await expect(field('软件名称')).toBeDisabled(); await expect(page.getByRole('button', { name: '退出', exact: true })).toBeDisabled()
  await save('核实原操作'); assert.equal(writes.length, 2); assert.ok(writes[0].key && writes[0].key === writes[1].key && writes[0].body === writes[1].body); await page.unroute('**/api/v1/manage/software')
  await expect(page.getByRole('heading', { name: '验证上位机', exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: '修改软件资料', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '登记软件', exact: true }).click(); await field('软件代码').fill('BROWSER-VISION'); await field('软件名称').fill('验证视觉'); await field('软件分类').selectOption('Vision'); await save()
  phase = 'factory process and device navigation'
  await nav('现场台账'); await expect(page.getByRole('navigation', { name: '现场层级' })).toContainText('浏览器验证厂区')
  await page.getByRole('button', { name: '创建工序', exact: true }).click(); await field('工序代码').fill('BROWSER-P'); await field('工序名称').fill('验证工序'); await save()
  await page.getByRole('button', { name: '创建设备', exact: true }).click(); await field('设备编号').fill('BROWSER-D'); await field('设备名称').fill('验证工序二期设备'); await save()
  await expect(page.getByRole('heading', { name: '验证工序二期设备', exact: true })).toBeVisible()
  const bind = async name => { await page.getByRole('button', { name: '关联软件', exact: true }).click(); await expect(field('选择关联软件').locator('option')).toHaveCount(3); await field('选择关联软件').selectOption({ label: name }); await reason('明确设备与软件关联'); await save() }
  await bind('验证上位机 · BROWSER-UPPER · 上位机'); await bind('验证视觉 · BROWSER-VISION · 视觉')
  await expect(page.locator('.inventory-card')).toHaveCount(2); await expect(page.locator('.inventory-card .badge')).toHaveText(['尚未登记', '尚未登记'])
  assert.equal(await page.locator('.inventory-card script').count(), 0)
  phase = 'logical revocation and reactivation'
  await page.locator('.mapping-section li').filter({ hasText: '验证视觉' }).getByRole('button', { name: '撤销映射' }).click(); await reason('撤销后重建验证'); await save('确认撤销'); await expect(page.locator('.inventory-card')).toHaveCount(1)
  await bind('验证视觉 · BROWSER-VISION · 视觉'); await expect(page.locator('.mapping-section li').filter({ hasText: '验证视觉' })).toContainText('关联修订 3')
  phase = 'device update and stale revision conflict'
  await page.getByRole('button', { name: '修改设备', exact: true }).click(); await field('设备名称').fill('未提交设备名'); await reason('并发验证')
  const other = await context.newPage(); other.setDefaultTimeout(12000); await other.goto(config.url + '/site'); await other.getByRole('button', { name: '验证工序 BROWSER-P' }).click(); await other.getByRole('button', { name: '验证工序二期设备 BROWSER-D' }).click(); await other.getByRole('button', { name: '修改设备', exact: true }).click()
  await other.getByRole('dialog').getByLabel('设备名称', { exact: true }).fill('已确认设备名'); await other.getByLabel('操作原因').fill('另一窗口更正'); await other.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click(); await expect(other.getByRole('dialog')).toHaveCount(0)
  await dialog().getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog().getByRole('alert')).toContainText('资料已被修改'); await dialog().getByRole('button', { name: '加载最新详情' }).click(); await expect(field('设备名称')).toHaveValue('已确认设备名'); await dialog().getByRole('button', { name: '取消', exact: true }).click(); await other.close()
  await page.getByRole('button', { name: '修改工序', exact: true }).click(); await field('工序名称').fill('已确认工序名'); await reason('更正工序显示名称'); await save()
  phase = 'software scope grants without implicit selection'
  await nav('人员账号'); await page.getByRole('button', { name: '测试管理员', exact: true }).click(); await page.getByRole('button', { name: '维护厂级权限与软件授权' }).click()
  await field('选择授权软件').selectOption({ label: '验证上位机 · BROWSER-UPPER · 上位机' }); await expect(dialog().getByLabel('维护软件资料及上传版本')).not.toBeChecked(); await expect(dialog().getByLabel('转正式', { exact: true })).not.toBeChecked(); await dialog().getByLabel('维护软件资料及上传版本').check(); await reason('分配资料维护'); await save()
  await nav('软件目录'); await page.getByRole('button', { name: '验证上位机', exact: true }).click(); await page.getByRole('button', { name: '修改软件资料', exact: true }).click(); await field('软件名称').fill('已确认上位机'); await field('软件说明').fill('资料更正'); await reason('更正软件资料'); await save()
  await expect(page.getByRole('heading', { name: '已确认上位机', exact: true })).toBeVisible()
  phase = 'empty and failed reads'
  await page.getByLabel('软件名称', { exact: true }).fill('不存在的软件'); await page.getByRole('button', { name: '查询', exact: true }).click(); await expect(page.getByText('没有可查看的软件。')).toBeVisible()
  await page.getByLabel('软件名称', { exact: true }).fill(''); await page.route('**/api/v1/manage/software?**', route => route.abort('failed')); await page.getByRole('button', { name: '查询', exact: true }).click(); await expect(page.getByRole('alert')).toContainText('连接中断'); await page.unroute('**/api/v1/manage/software?**'); await page.getByRole('button', { name: '重新读取', exact: true }).click(); await expect(page.getByRole('button', { name: '已确认上位机', exact: true })).toBeVisible()
  phase = 'permission revocation updates device inventory'
  await nav('人员账号'); await page.getByRole('button', { name: '测试管理员', exact: true }).click(); await page.getByRole('button', { name: '维护厂级权限与软件授权' }).click(); await field('选择授权软件').selectOption({ label: '验证视觉 · BROWSER-VISION · 视觉' }); await dialog().getByLabel('查看实例', { exact: true }).uncheck(); await reason('撤销视觉运行查看'); await save()
  await nav('现场台账'); await page.getByRole('button', { name: '已确认工序名 BROWSER-P' }).click(); await page.getByRole('button', { name: '已确认设备名 BROWSER-D' }).click(); await expect(page.locator('.inventory-card')).toHaveCount(1); await expect(page.locator('.inventory-card')).toContainText('已确认上位机'); await expect(page.locator('.mapping-section li')).toHaveCount(2)
  await expect(page.getByRole('button', { name: '修改设备', exact: true })).toBeEnabled()
  await expect(page.locator('.mapping-section li').filter({ hasText: '验证视觉' })).toContainText('BROWSER-VISION')
  phase = 'desktop mobile views and strict API fallback'
  await page.screenshot({ path: config.artifacts + '/site-desktop.png', fullPage: true }); await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: config.artifacts + '/site-mobile.png', fullPage: true }); assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
  const missing = await context.request.get(config.url + '/api/v1/unknown'); assert.equal(missing.status(), 404); assert.equal((await missing.json()).code, 'RESOURCE_NOT_FOUND')
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]); assert.equal(errors.length, 0)
  await page.getByRole('button', { name: '退出', exact: true }).click(); await expect(page.getByRole('heading', { name: '进入管理平台' })).toBeVisible()
  console.log('Browser catalog verification passed: hierarchy, explicit grants, software, device mappings, unknown replay, revision, filtering, errors and responsive HTTPS views.')
} catch (error) {
  if (page) await page.screenshot({ path: config.artifacts + '/failure.png', fullPage: true, mask: [page.locator('input[type="password"]')] }).catch(() => {})
  console.error('Browser catalog verification failed at ' + phase + ' (' + error.name + ').'); process.exitCode = 1
} finally { await browser?.close() }
