import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import App from '../src/App.vue'
import LoginPage from '../src/pages/LoginPage.vue'
import PasswordPage from '../src/pages/PasswordPage.vue'
import UsersPage from '../src/pages/UsersPage.vue'
import { useMutation } from '../src/mutation'
import { useSession } from '../src/session'

afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = '' })
const snapshot = (mustChangePassword: boolean, permissions: object[]) => ({ authenticated: true, subjectId: 'fixture-subject',
  employeeNo: 'FIXTURE', displayName: '测试人员', mustChangePassword, permissions, csrfToken: 'fixture-token', serverTime: '2026-10-07T00:00:00Z' })
async function mount(session: object) {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json(session)))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/users', component: UsersPage },
    { path: '/login', component: LoginPage }, { path: '/password', component: PasswordPage }] })
  await router.push('/users')
  const root = document.createElement('div'); document.body.appendChild(root)
  const app = createApp(App).use(createPinia()).use(router); app.mount(root)
  await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/password'))
  await nextTick()
  return { root, app }
}
describe('personnel access', () => {
  it('uses applied filters for paging and resets the cursor when querying edited filters', async () => {
    const fetch = vi.fn(async (_path: string) => Response.json({ items: [], nextCursor: 'fixture-next', serverTime: '2026-10-07T00:00:00Z' }))
    vi.stubGlobal('fetch', fetch)
    const pinia = createPinia()
    useSession(pinia).current = snapshot(false, [{ softwareId: null, operation: 'identity.manage' }]) as never
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/users', component: UsersPage }] })
    await router.push('/users')
    const root = document.createElement('div'); document.body.appendChild(root)
    const app = createApp({ template: '<RouterView />' }).use(pinia).use(router); app.mount(root)
    try {
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
      const input = root.querySelector('form input') as HTMLInputElement
      const status = root.querySelector('form select') as HTMLSelectElement
      input.value = 'NEW'; input.dispatchEvent(new Event('input', { bubbles: true }))
      status.value = 'false'; status.dispatchEvent(new Event('change', { bubbles: true })); await nextTick()
      const next = [...root.querySelectorAll('button')].find(button => button.textContent === '下一页')!
      await vi.waitFor(() => expect(next.disabled).toBe(false)); next.click()
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
      const pageQuery = new URL(String(fetch.mock.calls[1]?.[0]), 'https://fixture.invalid').searchParams
      expect(pageQuery.get('cursor')).toBe('fixture-next'); expect(pageQuery.has('employeeNo')).toBe(false); expect(pageQuery.has('isEnabled')).toBe(false)
      root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(3))
      const searchQuery = new URL(String(fetch.mock.calls[2]?.[0]), 'https://fixture.invalid').searchParams
      expect(searchQuery.has('cursor')).toBe(false); expect(searchQuery.get('employeeNo')).toBe('NEW'); expect(searchQuery.get('isEnabled')).toBe('false')
    } finally { app.unmount() }
  })
  it('restricts the first-change session to password and exit', async () => {
    const { root, app } = await mount(snapshot(true, [{ softwareId: null, operation: 'identity.manage' }]))
    expect(root.textContent).toContain('首次登录，请修改密码')
    expect(root.querySelector('nav')).toBeNull()
    expect(root.textContent).not.toContain('创建账号')
    app.unmount()
  })
  it('does not expose management to a person without the global grant', async () => {
    const { root, app } = await mount(snapshot(false, [{ softwareId: 'fixture-software', operation: 'identity.manage' }]))
    expect(root.textContent).toContain('当前账号未获管理或台账权限')
    expect(root.textContent).not.toContain('创建账号')
    app.unmount()
  })
  it('clears stale authority when the renewed session read fails', async () => {
    const pinia = createPinia(); const store = useSession(pinia)
    store.current = snapshot(false, [{ softwareId: null, operation: 'identity.manage' }]) as never
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(Response.json({ code: 'AUTHENTICATION_REQUIRED' }, { status: 401 }))
      .mockRejectedValueOnce(new Error('fixture unavailable')))
    await store.load()
    expect(store.current).toBeNull(); expect(store.canManage).toBeFalsy(); expect(store.error).not.toBe('')
  })
})
describe('uncertain management writes', () => {
  it('retains exactly the original request and key without automatic resend', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(Response.json({ code: 'DEPENDENCY_UNAVAILABLE' }, { status: 503 }))
      .mockResolvedValueOnce(Response.json({ id: 'fixture-user' }))
    vi.stubGlobal('fetch', fetch)
    const mutation = useMutation()
    await mutation.perform('/api/v1/manage/users', 'POST', { temporaryPassword: 'fixture memory secret' }, 'csrf')
    expect(fetch).toHaveBeenCalledTimes(1); expect(mutation.uncertain.value).toBe(true)
    await mutation.perform('/wrong-path', 'PATCH', { temporaryPassword: 'changed' }, 'csrf')
    expect(fetch.mock.calls[1]?.[0]).toBe(fetch.mock.calls[0]?.[0])
    const first = fetch.mock.calls[0]?.[1] as RequestInit, second = fetch.mock.calls[1]?.[1] as RequestInit
    expect(second.body).toBe(first.body); expect(second.headers).toEqual(first.headers)
    expect(mutation.uncertain.value).toBe(false)
    expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0)
  })
  it('blocks double submission while the first request is pending', async () => {
    let finish!: (value: Response) => void
    const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve })); vi.stubGlobal('fetch', fetch)
    const mutation = useMutation()
    const first = mutation.perform('/api/v1/manage/users', 'POST', {}, 'csrf')
    await mutation.perform('/api/v1/manage/users', 'POST', {}, 'csrf')
    expect(fetch).toHaveBeenCalledTimes(1)
    finish(Response.json({ id: 'fixture-user' })); await first
    expect(mutation.busy.value).toBe(false)
  })
  it('keeps an uncertain original operation when verification loses current authority', async () => {
    const fetch = vi.fn().mockRejectedValueOnce(new Error('fixture disconnected'))
      .mockResolvedValueOnce(Response.json({ code: 'AUTHENTICATION_REQUIRED' }, { status: 401 }))
      .mockResolvedValueOnce(Response.json({ code: 'PERMISSION_DENIED' }, { status: 403 }))
      .mockResolvedValueOnce(Response.json({ id: 'fixture-user' }))
    vi.stubGlobal('fetch', fetch)
    const mutation = useMutation()
    await mutation.perform('/api/v1/manage/users', 'POST', { displayName: 'original' }, 'csrf')
    const original = mutation.pending.value
    for (let i = 0; i < 2; i++) {
      await mutation.perform('/different', 'PATCH', {}, 'csrf')
      expect(mutation.pending.value).toBe(original); expect(mutation.uncertain.value).toBe(true)
      expect(mutation.error.value).toContain('无法核实')
    }
    expect(fetch).toHaveBeenCalledTimes(3)
    await mutation.perform('/different', 'PATCH', {}, 'csrf')
    const requests = fetch.mock.calls.map(call => call[1] as RequestInit)
    expect(requests.every(request => request.body === requests[0]?.body && JSON.stringify(request.headers) === JSON.stringify(requests[0]?.headers))).toBe(true)
    expect(mutation.uncertain.value).toBe(false)
  })
  it('does not report a revision conflict as success', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ code: 'REVISION_CONFLICT' }, { status: 409 })))
    const mutation = useMutation()
    expect(await mutation.perform('/api/v1/manage/users/id', 'PATCH', {}, 'csrf')).toBeUndefined()
    expect(mutation.error.value).toContain('重新加载'); expect(mutation.uncertain.value).toBe(false)
  })
})
