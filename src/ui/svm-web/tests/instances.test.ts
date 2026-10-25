import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref, type Component } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import InstancesPage from '../src/pages/InstancesPage.vue'
import EnrollmentPage from '../src/pages/EnrollmentPage.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
const sid = '11111111-1111-4111-8111-111111111111', iid = '22222222-2222-4222-8222-222222222222', did = '33333333-3333-4333-8333-333333333333'
const session = { authenticated: true, subjectId: did, employeeNo: 'FIXTURE', displayName: '夹具', mustChangePassword: false, csrfToken: 'fixture', serverTime: '2026-10-07T00:00:00Z', permissions: ['instance.read', 'instance.manage', 'enrollment.manage'].map(operation => ({ softwareId: sid, operation })) }
const instance = { id: iid, softwareId: sid, deviceId: did, deviceNo: 'FIXTURE-D', deviceName: '夹具设备', location: { siteId: did, siteName: '夹具厂区', processId: did, processCode: 'P', processName: '夹具工序' }, lifecycle: 'Active', revision: 1, lastSnapshot: null, lastAcceptedAt: null, unreportedSeconds: null, freshness: 'NeverReported', latestAvailableFormalReleaseId: null, latestTaskId: null, latestTaskResult: null }
const dispose: (() => void)[] = []
afterEach(() => { dispose.splice(0).forEach(f => f()); vi.unstubAllGlobals(); vi.restoreAllMocks(); document.body.innerHTML = ''; localStorage.clear(); sessionStorage.clear() })
async function mount(component: Component) { const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/page', component }, { path: '/other', component: { template: '<p>other</p>' } }] }); await router.push('/page?softwareId=' + sid + '&deviceId=' + did); const pinia = createPinia(); useSession(pinia).current = session; const root = document.createElement('div'); document.body.appendChild(root); const pending = ref(false); const app = createApp({ template: '<RouterView />' }).use(pinia).use(router).provide(pendingWriteKey, pending); app.mount(root); dispose.push(() => app.unmount()); return { router, pending } }
async function settle() { for (let i = 0; i < 15; i++) { await nextTick(); await Promise.resolve() } }
function button(text: string) { const x = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === text); expect(x).toBeTruthy(); return x! }
async function click(text: string) { button(text).click(); await settle() }
function fill(label: string, value: string) { const el = [...document.querySelectorAll('label')].find(l => l.textContent?.startsWith(label))!.querySelector('input,textarea,select') as HTMLInputElement; el.value = value; el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })) }
async function submit(text: string) { button(text).closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); await settle() }
const page = (items: unknown[], nextCursor: string | null = null) => Response.json({ items, nextCursor, serverTime: session.serverTime })
describe('instance interactions', () => {
  it('distinguishes never reported and stale facts without inventing version or live running status', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => page([instance, { ...instance, id: did, freshness: 'Unknown', lastAcceptedAt: '2026-10-07T00:00:00Z', unreportedSeconds: 301, lastSnapshot: { installedVersion: '2.4.0', runningState: 'Stopped', reportedIps: ['192.0.2.10'] } }])))
    await mount(InstancesPage); await settle(); expect(document.body.textContent).toContain('尚未上报'); expect(document.body.textContent).toContain('状态未知'); expect(document.body.textContent).toContain('2.4.0'); expect(document.body.textContent).toContain('192.0.2.10'); expect(document.body.textContent).toContain('暂停只限制 API')
  })
  it('paging uses applied filters until the user explicitly submits changed filters', async () => {
    const paths: string[] = []; vi.stubGlobal('fetch', vi.fn(async (p: string) => { paths.push(p); return page([instance], 'protected-cursor') }))
    await mount(InstancesPage); await settle(); fill('设备编号', 'OLD'); await submit('查询'); fill('设备编号', 'NEW'); await click('下一页'); expect(paths[paths.length - 1]).toContain('deviceNo=OLD'); expect(paths[paths.length - 1]).not.toContain('NEW'); await submit('查询'); expect(paths[paths.length - 1]).toContain('deviceNo=NEW'); expect(paths[paths.length - 1]).not.toContain('cursor=')
  })
  it('an uncertain lifecycle write keeps its original key and body and blocks navigation', async () => {
    const writes: RequestInit[] = []; vi.stubGlobal('fetch', vi.fn(async (p: string, o?: RequestInit) => { if (o?.method === 'PATCH') { writes.push(o); return writes.length === 1 ? Response.json({ code: 'DEPENDENCY_UNAVAILABLE' }, { status: 503 }) : Response.json({ ...instance, lifecycle: 'Suspended', revision: 2 }) }; if (p.endsWith('/session')) return Response.json(session); if (p.includes('version-history') || p.includes('/credentials?')) return page([]); if (p.endsWith('/' + iid)) return Response.json(instance); return page([instance]) }))
    const { router, pending } = await mount(InstancesPage); await settle(); await click('查看实例'); await click('暂停 API 接入'); fill('操作原因', 'fixture pause'); await submit('确认提交'); expect(pending.value).toBe(true); await router.push('/other'); expect(router.currentRoute.value.path).toBe('/page'); await submit('核实原操作'); expect(writes).toHaveLength(2); expect(writes[1].headers).toEqual(writes[0].headers); expect(writes[1].body).toEqual(writes[0].body); expect(pending.value).toBe(false)
  })
  it('shows legacy and fixed grant history without allowing broad enrollment creation', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => page([{ id: iid, softwareId: sid, state: 'Active', expiresAt: session.serverTime, deviceIds: [did], maxInstances: 2, usedCount: 1, revision: 1, hardwareBindingId: null }])))
    await mount(EnrollmentPage); await settle()
    expect(document.body.textContent).toContain('历史宽范围（不可新增登记）'); expect(document.body.textContent).toContain('新许可从具体工控机的软件位置签发')
    expect(document.body.textContent).not.toContain('签发登记许可'); expect(document.querySelector('input[type=password]')).toBeNull()
  })
  it('revokes only the frozen grant and preserves its original operation after response loss', async () => {
    vi.spyOn(HTMLDialogElement.prototype, 'showModal').mockImplementation(function () { this.open = true })
    vi.spyOn(HTMLDialogElement.prototype, 'close').mockImplementation(function () { this.open = false })
    const writes: { path: string; options: RequestInit }[] = []
    const grant = { id: iid, softwareId: sid, state: 'Active', expiresAt: session.serverTime, deviceIds: [did], maxInstances: 1, usedCount: 0, revision: 4, hardwareBindingId: did }
    vi.stubGlobal('fetch', vi.fn(async (p: string, o?: RequestInit) => {
      if (o?.method === 'POST') { writes.push({ path: p, options: o }); if (writes.length === 1) throw new TypeError('fixture response loss'); return Response.json({ ...grant, state: 'Revoked', revision: 5 }) }
      if (p.endsWith('/session')) return Response.json(session); return page([grant])
    }))
    const { router, pending } = await mount(EnrollmentPage); await settle(); await click('撤销'); fill('原因', '固定许可撤销'); await submit('确认撤销')
    expect(pending.value).toBe(true); await router.push('/other'); expect(router.currentRoute.value.path).toBe('/page')
    await submit('核实原操作'); expect(writes).toHaveLength(2); expect(writes[0].path).toBe(`/api/v1/manage/enrollment-grants/${iid}/revoke`)
    expect(writes[0].options.body).toBe(JSON.stringify({ expectedRevision: 4, reason: '固定许可撤销' })); expect(writes[0].path).toBe(writes[1].path); expect(writes[0].options.body).toBe(writes[1].options.body); expect(writes[0].options.headers).toEqual(writes[1].options.headers); expect(pending.value).toBe(false)
  })
})
