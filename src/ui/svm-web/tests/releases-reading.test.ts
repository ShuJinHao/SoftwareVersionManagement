import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import ReleasesPage from '../src/pages/ReleasesPage.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
vi.mock('../src/packages/hashFile', () => ({ hashFile: vi.fn(async () => 'a'.repeat(64)) }))

const sid = '11111111-1111-4111-8111-111111111111', a = '22222222-2222-4222-8222-222222222222', b = '33333333-3333-4333-8333-333333333333'
const releases = [a, b].map((id, index) => ({ id, softwareId: sid, version: `1.0.${index}`, state: 'Test', changeLevel: 'Patch', changeSummary: `版本详情 ${index}`, changeReason: '夹具', packageId: `package-${id}`, revision: 1, createdAt: '2026-10-16T00:00:00Z' }))
const account = (operations = ['software.read', 'audit.read']) => ({ authenticated: true, subjectId: sid, employeeNo: 'READ-FIXTURE', mustChangePassword: false, csrfToken: 'fixture', serverTime: '2026-10-16T00:00:00Z', permissions: operations.map(operation => ({ softwareId: sid, operation })) })
const packageInfo = (id: string) => ({ id: `package-${id}`, releaseId: id, state: 'Ready', processingStage: 'Copying', expectedSize: 1, sizeBytes: 1, expectedSha256: 'a'.repeat(64), sha256: 'a'.repeat(64), healthyReplicaCount: 2, downloadAvailable: false, revision: 1 })
const proof = (id: string, index: number) => ({ id: `${id}-proof-${index}`, releaseId: id, installedVersion: '1.0.0', instanceId: `proof-${index}-${id}`, receivedAt: '2026-10-16T00:00:00Z', reportedRunningState: 'Stopped' })
const audit = (id: string, index: number) => ({ requestId: `${id}-audit-${index}`, packageId: `package-${id}`, employeeNo: `audit-${index}-${id}`, state: 'Completed', bytesSent: 1, startedAt: '2026-10-16T00:00:00Z' })
const page = (items: unknown[], nextCursor: string | null = null) => Response.json({ items, nextCursor, serverTime: '2026-10-16T00:00:00Z' })
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no }); return { promise, resolve, reject } }
const disposers: (() => void)[] = []
afterEach(() => { disposers.splice(0).forEach(dispose => dispose()); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = '' })
async function settle() { for (let index = 0; index < 40; index++) { await nextTick(); await Promise.resolve() } }
function button(text: string) { const found = [...document.querySelectorAll('button')].find(item => item.textContent?.trim() === text); expect(found).toBeTruthy(); return found! }
async function mount(operations?: string[]) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/releases', component: ReleasesPage }, { path: '/other', component: { template: '<p>另一页面</p>' } }] })
  await router.push('/releases?softwareId=' + sid)
  const pinia = createPinia(), session = useSession(pinia); session.current = account(operations) as typeof session.current
  const element = document.createElement('div'); document.body.appendChild(element)
  const app = createApp({ template: '<RouterView />' }).use(pinia).use(router).provide(pendingWriteKey, ref(false)); app.mount(element)
  disposers.push(() => app.unmount()); await settle(); return { router, session }
}
async function startUpload() {
  button('登记版本并上传').click(); await settle()
  const textareas = document.querySelectorAll<HTMLTextAreaElement>('.upload-panel textarea')
  for (const textarea of textareas) { textarea.value = '本次上传夹具'; textarea.dispatchEvent(new Event('input', { bubbles: true })) }
  const input = document.querySelector<HTMLInputElement>('.upload-panel input[type=file]')!
  Object.defineProperty(input, 'files', { configurable: true, value: [new File(['x'], 'fixture.zip')] }); input.dispatchEvent(new Event('change', { bubbles: true })); await settle()
  document.querySelector('.upload-panel form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); await settle()
}
const uploadRegistration = { release: { ...releases[0], state: 'Staging' }, uploadId: 'fixture-upload', uploadPath: '/api/v1/manage/uploads/fixture-upload/content' }
function reads(override?: (path: string, options?: RequestInit) => Response | Promise<Response> | undefined) {
  vi.stubGlobal('fetch', vi.fn(async (path: string, options?: RequestInit) => {
    const value = override?.(path, options); if (value) return value
    if (path.endsWith('/session')) return Response.json(account())
    if (path.endsWith('/release-source')) return Response.json({ softwareId: sid, kind: 'Manual', enabled: false })
    if (path.endsWith('/capabilities')) return Response.json({ maxPackageBytes: 100000 })
    if (path.endsWith('/software/' + sid)) return Response.json({ id: sid, code: 'READ', name: '读取夹具软件', category: 'Vision' })
    const release = releases.find(item => path.endsWith('/releases/' + item.id)); if (release) return Response.json(release)
    const pkg = releases.find(item => path.endsWith('/packages/package-' + item.id)); if (pkg) return Response.json(packageInfo(pkg.id))
    const evidence = releases.find(item => path.includes('/releases/' + item.id + '/test-evidence')); if (evidence) return page([proof(evidence.id, 1)])
    if (path.includes('/audit-events?')) return page([audit(a, 1), audit(b, 1)])
    return page(releases)
  }))
}

describe('release read ownership', () => {
  it.each(['success', 'failure'])('keeps a complete B detail when the earlier A package ends with %s', async outcome => {
    const old = deferred<Response>()
    reads(path => path.endsWith('/packages/package-' + a) ? old.promise : undefined)
    await mount(); button('1.0.0').click(); await settle()
    expect(document.querySelector('aside h2')).toBeNull()
    button('1.0.1').click(); await settle()
    expect(document.querySelector('aside h2')?.textContent).toContain('1.0.1')
    if (outcome === 'success') old.resolve(Response.json(packageInfo(a))); else old.reject(new Error('旧版本失败'))
    await settle()
    expect(document.querySelector('aside')?.textContent).toContain('proof-1-' + b)
    expect(document.querySelector('aside')?.textContent).not.toContain('proof-1-' + a)
    expect(document.body.textContent).not.toContain('旧版本失败')
    expect(document.querySelector('aside h2')?.textContent).toContain('1.0.1')
  })

  it('discards late evidence and audit pages after the selected release changes', async () => {
    const evidence = deferred<Response>(), audits = deferred<Response>()
    reads(path => {
      if (path.includes('/test-evidence') && path.includes('cursor=')) return evidence.promise
      if (path.includes('/releases/' + a + '/test-evidence')) return page([proof(a, 1)], 'proof-next')
      if (path.includes('/audit-events?') && path.includes('cursor=')) return audits.promise
      if (path.includes('/audit-events?')) return page([audit(a, 1), audit(b, 1)], 'audit-next')
    })
    await mount(); button('1.0.0').click(); await settle(); button('更多安装证据').click(); button('更多下载记录').click(); await settle()
    button('1.0.1').click(); await settle()
    evidence.resolve(page([proof(a, 2)])); audits.resolve(page([audit(a, 2)])); await settle()
    expect(document.querySelector('aside')?.textContent).toContain('proof-1-' + b)
    expect(document.querySelector('aside')?.textContent).not.toContain('proof-2-' + a)
    expect(document.querySelector('aside')?.textContent).not.toContain('audit-2-' + a)
  })

  it('keeps three evidence and audit pages across polling and prevents duplicate concurrent pages', async () => {
    vi.useFakeTimers()
    const proofPage = deferred<Response>(); let held = false
    reads(path => {
      const url = new URL(path, 'https://fixture.invalid')
      if (path.includes('/test-evidence')) {
        const cursor = url.searchParams.get('cursor')
        if (cursor === 'proof-2' && !held) { held = true; return proofPage.promise }
        return page([proof(a, cursor === 'proof-3' ? 3 : 1)], cursor === 'proof-3' ? null : 'proof-2')
      }
      if (path.includes('/audit-events?')) {
        const cursor = url.searchParams.get('cursor'), index = cursor === 'audit-3' ? 3 : cursor === 'audit-2' ? 2 : 1
        return page([audit(a, index)], index === 3 ? null : `audit-${index + 1}`)
      }
    })
    await mount(); button('1.0.0').click(); await settle()
    const more = button('更多安装证据'); more.click(); more.click(); await settle()
    expect(vi.mocked(fetch).mock.calls.filter(([path]) => String(path).includes('cursor=proof-2'))).toHaveLength(1)
    proofPage.resolve(page([proof(a, 1), proof(a, 2)], 'proof-3')); await settle(); button('更多安装证据').click(); await settle()
    button('更多下载记录').click(); await settle(); button('更多下载记录').click(); await settle()
    await vi.advanceTimersByTimeAsync(10000); await settle()
    const text = document.querySelector('aside')?.textContent ?? ''
    for (const index of [1, 2, 3]) { expect(text).toContain(`proof-${index}-${a}`); expect(text).toContain(`audit-${index}-${a}`) }
    expect([...document.querySelectorAll('aside .permission-list li')].filter(item => item.textContent?.includes('proof-1-' + a))).toHaveLength(1)
    expect(vi.mocked(fetch).mock.calls.filter(([path]) => String(path).includes('/test-evidence'))).toHaveLength(3)
  })

  it('invalidates pending release reads immediately on permission revocation', async () => {
    const old = deferred<Response>(); let aborted: AbortSignal | null = null
    reads((path, options) => { if (path.endsWith('/packages/package-' + a)) { aborted = options?.signal as AbortSignal; return old.promise } })
    const { session } = await mount(); button('1.0.0').click(); await settle()
    session.current = account([]) as typeof session.current; await settle()
    expect(aborted!.aborted).toBe(true)
    old.resolve(Response.json(packageInfo(a))); await settle()
    expect(document.querySelector('aside h2')).toBeNull(); expect(document.body.textContent).not.toContain('版本详情 0')
    expect(document.body.textContent).toContain('无权查看')
  })

  it('invalidates pending detail reads on unmount without a stale error or session refresh', async () => {
    const old = deferred<Response>(); let signal: AbortSignal | null = null
    reads((path, options) => { if (path.endsWith('/packages/package-' + a)) { signal = options?.signal as AbortSignal; return old.promise } })
    const { router } = await mount(); button('1.0.0').click(); await settle(); await router.push('/other'); await settle()
    expect(signal!.aborted).toBe(true)
    old.resolve(Response.json({ code: 'PERMISSION_DENIED' }, { status: 403 })); await settle()
    expect(document.body.textContent).toBe('另一页面')
    expect(vi.mocked(fetch).mock.calls.some(([path]) => String(path).endsWith('/session'))).toBe(false)
  })

  it('keeps the current detail read when the same subject refreshes an unchanged session', async () => {
    const old = deferred<Response>(); let signal: AbortSignal | null = null
    reads((path, options) => { if (path.endsWith('/packages/package-' + a)) { signal = options?.signal as AbortSignal; return old.promise } })
    const { session } = await mount(); button('1.0.0').click(); await settle()
    session.current = account() as typeof session.current; await settle()
    expect(signal!.aborted).toBe(false)
    old.resolve(Response.json(packageInfo(a))); await settle()
    expect(document.querySelector('aside h2')?.textContent).toContain('1.0.0')
    expect(document.querySelector('aside')?.textContent).toContain('proof-1-' + a)
    expect(document.querySelector('aside')?.textContent).toContain('audit-1-' + a)
  })

  it('does not let a late Test list replace the selected Formal page', async () => {
    const old = deferred<Response>(); let testReads = 0
    reads(path => { if (path.includes('/releases?') && path.includes('channel=Test') && ++testReads === 2) return old.promise; if (path.includes('channel=Formal')) return page([{ ...releases[1], state: 'Formal' }]) })
    await mount(); button('刷新列表').click(); await settle(); button('正式版本').click(); await settle()
    old.resolve(page([releases[0]], 'obsolete-cursor')); await settle()
    expect(button('正式版本').getAttribute('aria-pressed')).toBe('true')
    expect(document.querySelector('table')?.textContent).toContain('1.0.1'); expect(document.querySelector('table')?.textContent).not.toContain('1.0.0')
    expect(button('下一页').disabled).toBe(true)
  })

  it('does not restore package facts when upload verification loses permission', async () => {
    const verification = deferred<Response>(); let signal: AbortSignal | null = null
    reads((path, options) => {
      if (options?.method === 'POST') return Response.json(uploadRegistration)
      if (options?.method === 'PUT') return Promise.reject(new TypeError('upload response lost'))
      if (path.endsWith('/uploads/fixture-upload')) { signal = options?.signal as AbortSignal; return verification.promise }
    })
    const { session } = await mount(['software.read', 'release.upload']); await startUpload()
    button('核实接收结果').click(); await settle(); session.current = account([]) as typeof session.current; await settle()
    expect(signal!.aborted).toBe(true)
    verification.resolve(Response.json(packageInfo(a))); await settle()
    expect(document.querySelector('aside h2')).toBeNull(); expect(document.querySelector('aside')?.textContent).not.toContain('已核对副本')
    expect(document.body.textContent).not.toContain('已核实接收事实')
  })

  it('does not start upload or restore facts after registration switches subject', async () => {
    const registration = deferred<Response>()
    reads((_path, options) => options?.method === 'POST' ? registration.promise : undefined)
    const { session } = await mount(['software.read', 'release.upload']); await startUpload()
    session.current = { ...account(['software.read', 'release.upload']), subjectId: b } as typeof session.current; await settle()
    registration.resolve(Response.json(uploadRegistration)); await settle()
    expect(vi.mocked(fetch).mock.calls.some(([, options]) => options?.method === 'PUT')).toBe(false)
    expect(document.querySelector('aside h2')).toBeNull(); expect(document.body.textContent).not.toContain('已登记 1.0.0')
  })

  it('does not restore package facts when an upload response arrives after permission revocation', async () => {
    const upload = deferred<Response>()
    reads((_path, options) => options?.method === 'POST' ? Response.json(uploadRegistration) : options?.method === 'PUT' ? upload.promise : undefined)
    const { session } = await mount(['software.read', 'release.upload']); await startUpload()
    expect(vi.mocked(fetch).mock.calls.some(([, options]) => options?.method === 'PUT')).toBe(true)
    session.current = account([]) as typeof session.current; await settle(); upload.resolve(Response.json(packageInfo(a))); await settle()
    expect(document.querySelector('aside h2')).toBeNull(); expect(document.querySelector('aside')?.textContent).not.toContain('已核对副本')
    expect(document.body.textContent).not.toContain('服务器已核对接收内容')
  })
})
