import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import ReleasesPage from '../src/pages/ReleasesPage.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'

const sid = '11111111-1111-4111-8111-111111111111', rid = '22222222-2222-4222-8222-222222222222', pid = '33333333-3333-4333-8333-333333333333', eid = '44444444-4444-4444-8444-444444444444'
const release = { id: rid, softwareId: sid, version: '1.0.0', state: 'Test', changeLevel: 'Patch', changeSummary: '已安装待发布', changeReason: 'fixture', packageId: pid, revision: 2, createdAt: '2026-10-09T00:00:00Z', publishedAt: null }
const publication = { ...release, state: 'Formal', revision: 3, publishedBy: eid, publishedEmployeeNo: 'PUB-FIXTURE', publishedAt: '2026-10-09T01:00:00Z', testEvidenceId: eid, publishReason: '测试通过', publishConclusion: '安装检查通过' }
const pkg = { id: pid, state: 'Ready', processingStage: 'Completed', sizeBytes: 123, expectedSize: 123, sha256: 'a'.repeat(64), expectedSha256: 'a'.repeat(64), healthyReplicaCount: 2, downloadAvailable: true, downloadPath: '/api/v1/packages/' + pid + '/content' }
const evidence = { id: eid, instanceId: 'fixture-instance', releaseId: rid, installedVersion: '1.0.0', receivedAt: '2026-10-08T00:00:00Z', reportedRunningState: 'Stopped' }
function session(publish = true) { return { authenticated: true, subjectId: eid, employeeNo: 'PUB-FIXTURE', mustChangePassword: false, csrfToken: 'fixture', permissions: (publish ? ['software.read', 'release.publish'] : ['software.read']).map(operation => ({ softwareId: sid, operation })) } }
const disposers: (() => void)[] = []
afterEach(() => { disposers.splice(0).forEach(f => f()); vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = '' })
async function settle() { for (let i = 0; i < 25; i++) { await nextTick(); await Promise.resolve() } }
function button(text: string) { const b = [...document.querySelectorAll('button')].find(x => x.textContent?.trim() === text); expect(b).toBeTruthy(); return b! }
function fill(selector: string, value: string) { const input = document.querySelector(selector) as HTMLInputElement; expect(input).toBeTruthy(); input.value = value; input.dispatchEvent(new Event(selector === 'select' ? 'change' : 'input', { bubbles: true })) }
async function mount(publish = true, formal = false) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/releases', component: ReleasesPage }, { path: '/other', component: { template: '<p>other</p>' } }] })
  await router.push('/releases?softwareId=' + sid + (formal ? '&channel=Formal' : '')); const pinia = createPinia(); useSession(pinia).current = session(publish) as ReturnType<typeof useSession>['current']
  const root = document.createElement('div'); document.body.appendChild(root); const pending = ref(false)
  const app = createApp({ template: '<RouterView />' }).use(pinia).use(router).provide(pendingWriteKey, pending); app.mount(root); disposers.push(() => app.unmount()); await settle(); button('1.0.0').click(); await settle()
  return { router, pending, account: useSession(pinia) }
}
function reads(options: { evidence?: boolean; copies?: number; publish?: boolean; published?: boolean; disabled?: boolean } = {}, write?: (p: string, o: RequestInit) => Promise<Response>) {
  let current = options.disabled ? { ...publication, state: 'Disabled', disabledAt: '2026-10-09T02:00:00Z', disableReason: '保留历史' } : options.published ? publication : release
  vi.stubGlobal('fetch', vi.fn(async (path: string, o?: RequestInit) => {
    if (o?.method && o.method !== 'GET' && write) { const r = await write(path, o); if (r.ok) current = await r.clone().json(); return r }
    if (path.endsWith('/session')) return Response.json(session(options.publish ?? true))
    if (path.endsWith('/capabilities')) return Response.json({ maxPackageBytes: 10000000 })
    if (path.endsWith('/software/' + sid)) return Response.json({ id: sid, name: '夹具视觉', code: 'PUB', category: 'Vision', latestAvailableFormalReleaseId: current.publishedAt ? rid : null })
    if (path.endsWith('/releases/' + rid)) return Response.json(current)
    if (path.endsWith('/packages/' + pid)) return Response.json({ ...pkg, healthyReplicaCount: options.copies ?? 2 })
    if (path.includes('test-evidence')) return Response.json({ items: options.evidence === false ? [] : [evidence], nextCursor: null })
    return Response.json({ items: path.includes('channel=Formal') === !!current.publishedAt ? [current] : [], nextCursor: null })
  }))
}
async function fillForm() { fill('select', eid); fill('.publication-panel input', '测试通过'); fill('.publication-panel textarea', '安装检查通过'); await settle() }

describe('release publication', () => {
  it('requires evidence, both replicas, conclusion and current permission before offering publication', async () => {
    reads({ evidence: false, copies: 1 }); await mount(); expect(button('转为正式版').disabled).toBe(true); expect(document.body.textContent).toContain('尚无可选择的安装证据'); expect(document.body.textContent).toContain('两个当前健康副本')
    expect(vi.mocked(fetch).mock.calls.every(([, o]) => !o?.method || o.method === 'GET')).toBe(true)
  })
  it('publishes a stopped historical installation and shows immutable formal details and latest entry', async () => {
    const writes: RequestInit[] = []; reads({}, async (path, o) => { expect(path).toBe('/api/v1/manage/releases/' + rid + '/publish'); writes.push(o); return Response.json(publication) })
    await mount(); await fillForm(); expect(button('转为正式版').disabled).toBe(false); button('转为正式版').click(); await settle()
    expect(writes).toHaveLength(1); expect(JSON.parse(writes[0].body as string)).toEqual({ testEvidenceId: eid, publishReason: '测试通过', publishConclusion: '安装检查通过', expectedRevision: 2 })
    expect(document.body.textContent).toContain('正式发布记录'); expect(document.body.textContent).toContain('PUB-FIXTURE'); expect(document.querySelector('a[download]')?.getAttribute('href')).toBe(pkg.downloadPath); expect(button('正式版本').getAttribute('aria-pressed')).toBe('true'); expect(button('查看最新可用正式版')).toBeTruthy()
  })
  it('retains original body and key on response loss and permission revocation without automatic retry', async () => {
    const writes: RequestInit[] = []; reads({ publish: false }, async (_path, o) => { writes.push(o); if (writes.length === 1) throw new TypeError('lost response'); return Response.json({ code: 'PERMISSION_DENIED' }, { status: 403 }) })
    const { pending, router } = await mount(); await fillForm(); button('转为正式版').click(); await settle(); expect(writes).toHaveLength(1); expect(pending.value).toBe(true)
    expect((document.querySelector('.publication-panel fieldset') as HTMLFieldSetElement).disabled).toBe(true); await router.push('/other'); expect(router.currentRoute.value.path).toBe('/releases')
    button('核实原发布操作').click(); await settle(); expect(writes).toHaveLength(2); expect(writes[1].headers).toEqual(writes[0].headers); expect(writes[1].body).toEqual(writes[0].body); expect(pending.value).toBe(true); expect(document.body.textContent).toContain('原操作')
  })
  it('revision rejection requires explicit reload and a revoked permission removes the form', async () => {
    reads({}, async () => Response.json({ code: 'REVISION_CONFLICT' }, { status: 409 })); const { pending, account } = await mount(); await fillForm(); button('转为正式版').click(); await settle(); expect(pending.value).toBe(false); expect(document.body.textContent).toContain('资料已被修改'); expect(button('加载最新详情')).toBeTruthy()
    account.current = session(false) as typeof account.current; await settle(); expect(document.querySelector('.publication-panel')).toBeNull()
  })
  it('disabled formal history retains publisher and conclusion without offering publication', async () => { reads({ disabled: true, publish: false }); await mount(false, true); expect(document.body.textContent).toContain('正式发布记录'); expect(document.body.textContent).toContain('PUB-FIXTURE'); expect(document.body.textContent).toContain('安装检查通过'); expect(document.body.textContent).toContain('已停用'); expect(document.querySelector('.publication-panel')).toBeNull() })
})
