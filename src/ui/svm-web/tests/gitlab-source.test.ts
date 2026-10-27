import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import ReleasesPage from '../src/pages/ReleasesPage.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
vi.mock('../src/packages/hashFile', () => ({ hashFile: vi.fn() }))
const sid = '11111111-1111-4111-8111-111111111111', connection = '22222222-2222-4222-8222-222222222222'
const option = { connectionId: connection, connectionName: '内网发布来源', baseUri: 'https://gitlab.example.invalid/', projectId: 17, projectName: '视觉项目', packageName: 'fixture' }
const source = { softwareId: sid, kind: 'Manual', enabled: false, revision: 0 }
const account = (ops = ['software.read', 'release.source.manage']) => ({ authenticated: true, subjectId: sid, employeeNo: 'SOURCE-FIXTURE', mustChangePassword: false, csrfToken: 'fixture', permissions: ops.map(operation => ({ softwareId: sid, operation })) })
const cleanup: (() => void)[] = []
afterEach(() => { cleanup.splice(0).forEach(x => x()); vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = '' })
async function settle() { for (let i = 0; i < 50; i++) { await nextTick(); await Promise.resolve() } }
function button(text: string) { const result = [...document.querySelectorAll('button')].find(x => x.textContent?.trim() === text); expect(result).toBeTruthy(); return result! }
function input(selector: string, value: string) { const element = document.querySelector<HTMLInputElement>(selector)!; element.value = value; element.dispatchEvent(new Event('input', { bubbles: true })) }
function fetches(override?: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined, ops?: string[]) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const response = override?.(url, init); if (response) return response
    if (url.endsWith('/session')) return Response.json(account(ops))
    if (url.includes('/release-source-options?')) return Response.json({ items: [option], nextPage: null, current: source })
    if (url.endsWith('/release-source')) return Response.json(source)
    if (url.endsWith('/software/' + sid)) return Response.json({ id: sid, name: '来源夹具软件', code: 'SW-1', category: 'Vision' })
    return Response.json({ items: [], nextCursor: null })
  }))
}
async function mount(ops?: string[]) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/releases', component: ReleasesPage }] }); await router.push('/releases?softwareId=' + sid)
  const pinia = createPinia(), session = useSession(pinia); session.current = account(ops) as typeof session.current
  const el = document.createElement('div'); document.body.appendChild(el)
  const app = createApp({ template: '<RouterView />' }).use(pinia).use(router).provide(pendingWriteKey, ref(false)); app.mount(el); cleanup.push(() => app.unmount()); await settle(); return session
}
async function draft() { button('关联 GitLab 来源').click(); await settle(); button('选择此来源').click(); input('dialog textarea', '来源调整夹具'); await settle() }
async function submit() { document.querySelector('dialog form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); await settle() }
describe('GitLab source management', () => {
  it('shows only allowed candidates and saves the fixed source revision without secrets', async () => {
    fetches((url, init) => init?.method === 'PUT' ? Response.json({ ...source, ...option, kind: 'GitLab', enabled: true, revision: 1 }) : undefined)
    await mount(); await draft(); expect(document.querySelector('dialog')?.textContent).toContain('视觉项目'); await submit()
    const [, request] = vi.mocked(fetch).mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(JSON.parse(request!.body as string)).toEqual({ connectionId: connection, projectId: 17, packageName: 'fixture', enabled: true, expectedRevision: 0, reason: '来源调整夹具' })
    expect(document.querySelector('dialog')).toBeNull(); expect(document.body.textContent).toContain('保存设置不代表 GitLab 已连接')
  })
  it('does not require software read permission to manage a known source', async () => {
    const ops = ['release.source.manage']; fetches((url, init) => init?.method === 'PUT' ? Response.json({ ...source, revision: 1 }) : undefined, ops)
    await mount(ops); await draft(); await submit()
    expect(vi.mocked(fetch).mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(true)
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).endsWith('/release-source'))).toBe(true)
    expect(vi.mocked(fetch).mock.calls.filter(([url, init]) => String(url).endsWith('/release-source') && !init?.method)).toHaveLength(0)
  })
  it('keeps the applied search when paging and prevents concurrent duplicate loads', async () => {
    let resolve!: (value: Response) => void
    const pending = new Promise<Response>(r => { resolve = r })
    fetches(url => url.includes('/release-source-options?') ? (url.includes('page=2') ? pending : Response.json({ items: [option], nextPage: 2, current: source })) : undefined)
    await mount(); await draft(); input('dialog input[placeholder]', '尚未查询的筛选'); button('更多来源').click(); button('更多来源').click(); await settle()
    const reads = vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('page=2')); expect(reads).toHaveLength(1); expect(new URL(reads[0][0] as string, 'https://fixture.invalid').searchParams.get('search')).toBe('')
    resolve(Response.json({ items: [option, { ...option, projectId: 18, projectName: '后续项目' }], nextPage: null, current: source })); await settle()
    expect(document.querySelectorAll('dialog tbody tr')).toHaveLength(2)
  })
  it('invalidates a fixed draft and discards late candidates when permission is revoked', async () => {
    let resolve!: (value: Response) => void; const old = new Promise<Response>(r => { resolve = r })
    fetches(url => url.includes('search=late') ? old : undefined)
    const session = await mount(); await draft(); input('dialog input[placeholder]', 'late'); button('查询来源').click(); await settle()
    session.current = account(['software.read']) as typeof session.current; await settle()
    resolve(Response.json({ items: [{ ...option, projectName: '不应显示的迟到项目' }], nextPage: null, current: source })); await settle(); await submit()
    expect(document.body.textContent).not.toContain('不应显示的迟到项目'); expect(document.body.textContent).toContain('人员或权限已变化')
    expect(vi.mocked(fetch).mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  })
  it('retains the original key and body after an unknown save until manual verification', async () => {
    let writes = 0
    fetches((url, init) => { if (init?.method === 'PUT') { if (++writes === 1) return Promise.reject(new TypeError('response lost')); return Response.json({ ...source, revision: 1 }) } })
    await mount(); await draft(); await submit(); expect(writes).toBe(1); expect(document.body.textContent).toContain('核实原来源操作')
    await submit(); expect(writes).toBe(2)
    const calls = vi.mocked(fetch).mock.calls.filter(([, init]) => init?.method === 'PUT'); expect(calls[1][1]?.body).toBe(calls[0][1]?.body); expect(calls[1][1]?.headers).toEqual(calls[0][1]?.headers)
  })
  it('keeps the original draft for revision conflict and explains busy work without claiming connection success', async () => {
    fetches((url, init) => init?.method === 'PUT' ? Response.json({ code: 'REVISION_CONFLICT' }, { status: 409 }) : undefined)
    await mount(); await draft(); await submit(); expect(document.body.textContent).toContain('来源已被修改'); expect(document.querySelector('dialog textarea')).not.toBeNull()
  })
})
