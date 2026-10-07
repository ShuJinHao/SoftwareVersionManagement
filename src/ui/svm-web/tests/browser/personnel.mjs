import { readFile, mkdir } from 'node:fs/promises'
import assert from 'node:assert/strict'
import { chromium } from 'playwright'
import { expect } from '@playwright/test'

// Only the owning Framework fixture supplies an ephemeral HTTPS host and credentials.
// No trace, request dump, or browser storage is used for passwords or session tokens.
const config = JSON.parse(await readFile(process.env.SVM_PERSONNEL_BROWSER_CONFIG_FILE, 'utf8'))
await mkdir(config.artifacts, { recursive: true })
let browser, page, phase = 'startup'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' })
  page = await context.newPage()
  page.setDefaultTimeout(12000)
  const errors = []
  page.on('pageerror', () => errors.push('pageerror'))
  const login = async (target, employeeNo, password) => {
    await target.goto(config.url + '/login')
    await target.getByLabel('工号', { exact: true }).fill(employeeNo)
    await target.getByLabel('密码', { exact: true }).fill(password)
    await target.getByRole('button', { name: '登录', exact: true }).click()
  }
  const change = async (target, current, next) => {
    await expect(target.getByRole('heading', { name: '首次登录，请修改密码' })).toBeVisible()
    await target.getByLabel('当前密码').fill(current)
    await target.getByLabel('新密码', { exact: true }).fill(next)
    await target.getByLabel('确认新密码').fill(next)
    await target.getByRole('button', { name: '修改密码', exact: true }).click()
  }
  const save = async () => {
    await page.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
  }
  const reason = value => page.getByLabel('操作原因').fill(value)
  phase = 'login and mandatory password change'
  await login(page, config.employeeNo, config.initialPassword)
  await expect(page.getByRole('navigation')).toHaveCount(0)
  await change(page, config.initialPassword, config.adminPassword)
  await page.getByRole('link', { name: '人员账号', exact: true }).click()
  await expect(page.getByRole('heading', { name: '人员账号', exact: true })).toBeVisible()
  await page.screenshot({ path: config.artifacts + '/accounts-desktop.png', fullPage: true })

  phase = 'lost creation response and manual replay'
  let drop = true
  const writes = []
  await page.route('**/api/v1/manage/users', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    writes.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() })
    if (drop) {
      drop = false
      const response = await route.fetch()
      assert.equal(response.status(), 201)
      await route.abort('failed')
    } else await route.continue()
  })
  await page.getByRole('button', { name: '创建账号' }).click()
  await page.getByLabel('工号', { exact: true }).fill('BROWSER-USER')
  await page.getByLabel('显示名称').fill('浏览器验证人员')
  await page.getByLabel('临时密码').fill(config.temporaryPassword)
  await page.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('button', { name: '核实原操作' })).toBeVisible()
  await page.waitForTimeout(600)
  assert.equal(writes.length, 1)
  await expect(page.getByLabel('临时密码')).toBeDisabled()
  await page.getByRole('button', { name: '核实原操作' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  assert.equal(writes.length, 2)
  assert.ok(writes[0].key && writes[0].key === writes[1].key && writes[0].body === writes[1].body)
  await page.unroute('**/api/v1/manage/users')
  await page.getByRole('button', { name: '浏览器验证人员', exact: true }).click()

  phase = 'factory grants and account editing'
  await page.getByRole('button', { name: '维护厂级权限' }).click()
  await page.getByLabel('查看现场台账').check()
  await page.getByLabel('维护现场台账').check()
  await reason('分配台账职责')
  await save()
  await expect(page.locator('.permission-list')).toContainText('维护现场台账')
  await page.getByRole('button', { name: '修改资料 / 启停' }).click()
  await page.getByLabel('显示名称').fill('浏览器管理人员')
  await reason('更新显示名称')
  await save()
  await expect(page.getByRole('heading', { name: '浏览器管理人员', exact: true })).toBeVisible()

  phase = 'reset and unprivileged mandatory change'
  await page.getByRole('button', { name: '重置密码', exact: true }).click()
  await page.getByLabel('临时密码').fill(config.resetPassword)
  await reason('核实身份后重置')
  await save()
  const personContext = await browser.newContext({ ignoreHTTPSErrors: true })
  const person = await personContext.newPage()
  person.setDefaultTimeout(12000)
  await login(person, 'BROWSER-USER', config.resetPassword)
  await expect(person.getByRole('navigation')).toHaveCount(0)
  await change(person, config.resetPassword, config.userPassword)
  await expect(person.getByRole('heading', { name: '现场台账', exact: true })).toBeVisible()
  await expect(person.getByRole('alert')).toContainText('服务配置不可用')
  await expect(person.getByRole('link', { name: '人员账号', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '修改资料 / 启停' }).click()
  await page.getByLabel('启用账号').uncheck()
  await reason('停用访问')
  await save()
  phase = 'disabled session navigation'
  await person.reload()
  await expect(person.getByRole('heading', { name: '进入管理平台' })).toBeVisible()
  await page.getByRole('button', { name: '修改资料 / 启停' }).click()
  await page.getByLabel('启用账号').check()
  await reason('恢复访问')
  await save()
  phase = 're-enabled account does not restore a session'
  await person.reload()
  await expect(person.getByRole('heading', { name: '进入管理平台' })).toBeVisible()
  await personContext.close()

  phase = 'revision conflict'
  await page.getByRole('button', { name: '修改资料 / 启停' }).click()
  await page.getByLabel('显示名称').fill('尚未提交的名称')
  await reason('并发资料更新')
  const other = await context.newPage()
  await other.goto(config.url + '/users')
  await other.getByRole('button', { name: '浏览器管理人员', exact: true }).click()
  await other.getByRole('button', { name: '修改资料 / 启停' }).click()
  await other.getByLabel('显示名称').fill('已确认的新名称')
  await other.getByLabel('操作原因').fill('另一窗口更新')
  await other.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
  await expect(other.getByRole('dialog')).toHaveCount(0)
  await page.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('资料已被修改')
  await page.getByRole('button', { name: '加载最新详情' }).click()
  await expect(page.getByRole('heading', { name: '已确认的新名称', exact: true })).toBeVisible()
  await expect(page.getByLabel('显示名称')).toHaveValue('已确认的新名称')
  await expect(page.getByLabel('操作原因')).toHaveValue('')
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await other.close()

  phase = 'last administrator protection'
  await page.getByRole('button', { name: '测试管理员', exact: true }).click()
  await page.getByRole('button', { name: '修改资料 / 启停' }).click()
  await page.getByLabel('启用账号').uncheck()
  await reason('验证保留管理员')
  await page.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('保留至少一个有效管理员')
  await page.getByRole('button', { name: '取消', exact: true }).click()

  phase = 'empty and failed lists'
  await page.getByLabel('工号筛选').fill('NOT-PRESENT')
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.getByText('没有符合条件的账号。')).toBeVisible()
  await page.getByLabel('工号筛选').fill('')
  await page.route('**/api/v1/manage/users?**', route => route.abort('failed'))
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.getByText('账号列表暂不可用')).toBeVisible()
  await page.unroute('**/api/v1/manage/users?**')
  await page.getByRole('button', { name: '重试读取', exact: true }).click()
  await expect(page.getByRole('button', { name: '已确认的新名称', exact: true })).toBeVisible()

  phase = 'responsive view and API fallback'
  await page.getByRole('button', { name: '已确认的新名称', exact: true }).click()
  await expect(page.getByRole('heading', { name: '已确认的新名称', exact: true })).toBeVisible()
  await page.screenshot({ path: config.artifacts + '/accounts-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: config.artifacts + '/accounts-mobile.png', fullPage: true })
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
  const missing = await context.request.get(config.url + '/api/v1/unknown')
  assert.equal(missing.status(), 404)
  assert.ok(missing.headers()['content-type'].startsWith('application/json'))
  assert.equal((await missing.json()).code, 'RESOURCE_NOT_FOUND')
  assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0])
  assert.equal(errors.length, 0)
  await page.getByRole('button', { name: '退出', exact: true }).click()
  await expect(page.getByRole('heading', { name: '进入管理平台' })).toBeVisible()
  console.log('Browser personnel verification passed: real HTTPS, lost response, replay, grants, reset, revocation, revision, empty/error states, responsive view.')
} catch (error) {
  if (page) await page.screenshot({ path: config.artifacts + '/failure.png', fullPage: true, mask: [page.locator('input[type="password"]')] }).catch(() => {})
  // Playwright diagnostics can contain filled values; never emit its raw exception or trace.
  console.error('Browser verification failed at ' + phase + ' (' + error.name + ').')
  process.exitCode = 1
} finally { await browser?.close() }
