import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { useMutation } from '../src/mutation'
import { useProtectedMutation } from '../src/protectedMutation'
import { useSession } from '../src/session'
import OperationStatus from '../src/components/OperationStatus.vue'

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); document.body.innerHTML = '' })
describe('fixed write verification', () => {
  it('blocks verification by a different person with identical permissions and resumes only for the original subject', async () => {
    const account = (subjectId: string) => ({ authenticated: true, subjectId, employeeNo: subjectId, displayName: subjectId, mustChangePassword: false,
      permissions: [{ softwareId: null, operation: 'identity.manage' }], csrfToken: 'csrf-' + subjectId, serverTime: '2026-10-16T00:00:00Z' })
    let cookieSubject = 'original', writes = 0, failSessionRead = false
    const fetch = vi.fn(async (path: string, _init?: RequestInit) => {
      if (path === '/api/v1/session') return failSessionRead ? Response.json({ code: 'DEPENDENCY_UNAVAILABLE' }, { status: 503 }) : Response.json(account(cookieSubject))
      if (++writes === 1) throw new Error('lost original result')
      return Response.json({ id: 'original-result' })
    }); vi.stubGlobal('fetch', fetch)
    let mutation!: ReturnType<typeof useProtectedMutation>
    const page = { setup() { mutation = useProtectedMutation(); return () => null } }
    const pinia = createPinia(), session = useSession(pinia); session.current = account('original')
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/fixture', component: page }] }); await router.push('/fixture')
    const root = document.createElement('div'); document.body.appendChild(root)
    const app = createApp({ template: '<RouterView />' }).use(pinia).use(router); app.mount(root)
    try {
      await mutation.perform('/fixture/write', 'POST', { reason: 'original' }, 'csrf-original')
      const original = mutation.pending.value!
      cookieSubject = 'other'; session.current = account('other')
      await mutation.perform('/different', 'PATCH', {}, 'csrf-other')
      expect(fetch).toHaveBeenCalledTimes(1); expect(writes).toBe(1)
      expect(mutation.pending.value).toBe(original); expect(mutation.errorCode.value).toBe('OPERATION_SUBJECT_CHANGED')
      // A stale original page must also inspect the current cookie identity before the POST.
      session.current = account('original')
      await mutation.perform('/different', 'PATCH', {}, 'csrf-original')
      expect(fetch).toHaveBeenCalledTimes(2); expect(writes).toBe(1); expect(mutation.pending.value).toBe(original)
      expect(mutation.error.value).toContain('原操作人员不一致')
      cookieSubject = 'original'; session.current = account('original')
      failSessionRead = true
      await mutation.perform('/different', 'PATCH', {}, 'csrf-original')
      expect(writes).toBe(1); expect(mutation.pending.value).toBe(original); expect(mutation.errorCode.value).toBe('CURRENT_SESSION_UNKNOWN')
      failSessionRead = false
      expect(await mutation.perform('/different', 'PATCH', {}, 'csrf-original')).toEqual({ id: 'original-result' })
      const calls = fetch.mock.calls.filter(call => call[0] === '/fixture/write')
      expect(calls).toHaveLength(2); expect(writes).toBe(2); expect(mutation.pending.value).toBeNull()
      const allWrites = fetch.mock.calls.filter(call => call[0] !== '/api/v1/session')
      expect(allWrites.every(call => call[0] === '/fixture/write')).toBe(true)
      expect(allWrites[1]?.[1]?.body).toBe(allWrites[0]?.[1]?.body)
      expect((allWrites[1]?.[1]?.headers as Record<string, string>)['Idempotency-Key']).toBe(original.key)
    } finally { app.unmount() }
  })
  it('serializes once and preserves nested values after the submitted form is mutated', async () => {
    const fetch = vi.fn().mockRejectedValueOnce(new Error('lost response')).mockResolvedValueOnce(Response.json({ id: 'fixture' }))
    vi.stubGlobal('fetch', fetch)
    const mutation = useMutation(), body = { permissions: [{ operation: 'software.read' }], reason: 'original' }
    await mutation.perform('/fixture/original', 'PUT', body, 'csrf')
    body.permissions[0]!.operation = 'changed'; body.permissions.push({ operation: 'extra' }); body.reason = 'changed'
    const operation = mutation.pending.value!
    expect(Object.isFrozen(operation.body)).toBe(true)
    await mutation.perform('/different', 'PATCH', { reason: 'different' }, 'new-csrf')
    const first = fetch.mock.calls[0]!, second = fetch.mock.calls[1]!
    expect(second[0]).toBe('/fixture/original')
    expect((second[1] as RequestInit).method).toBe('PUT')
    expect((second[1] as RequestInit).body).toBe((first[1] as RequestInit).body)
    expect(((second[1] as RequestInit).headers as Record<string, string>)['Idempotency-Key']).toBe(operation.key)
    expect(JSON.parse((second[1] as RequestInit).body as string)).toEqual({ permissions: [{ operation: 'software.read' }], reason: 'original' })
    expect(mutation.pending.value).toBeNull()
  })
  it('uses a single serialization even when the form supplies toJSON', async () => {
    const fetch = vi.fn().mockRejectedValueOnce(new Error('lost response')).mockResolvedValueOnce(Response.json({ id: 'fixture' }))
    vi.stubGlobal('fetch', fetch)
    const toJSON = vi.fn(() => ({ revision: 2 }))
    const mutation = useMutation()
    await mutation.perform('/fixture', 'POST', { toJSON }, 'csrf')
    await mutation.perform('/different', 'POST', {}, 'csrf')
    expect(toJSON).toHaveBeenCalledTimes(1)
    expect((fetch.mock.calls[1]?.[1] as RequestInit).body).toBe('{"revision":2}')
  })
  it('keeps an already-committed write after body timeout and verifies it only on user action', async () => {
    vi.useFakeTimers()
    const fetch = vi.fn().mockResolvedValueOnce({ ok: true, status: 200, headers: new Headers(), json: () => new Promise(() => {}) })
      .mockResolvedValueOnce(Response.json({ id: 'already-committed' }))
    vi.stubGlobal('fetch', fetch)
    const mutation = useMutation()
    const submission = mutation.perform('/fixture', 'POST', { expectedRevision: 7 }, 'csrf')
    const original = mutation.pending.value!
    await vi.advanceTimersByTimeAsync(30_000); await submission
    expect(mutation.uncertain.value).toBe(true); expect(mutation.errorCode.value).toBe('REQUEST_TIMEOUT')
    await vi.advanceTimersByTimeAsync(60_000); expect(fetch).toHaveBeenCalledTimes(1)
    expect(await mutation.perform('/different', 'PATCH', { expectedRevision: 8 }, 'csrf')).toEqual({ id: 'already-committed' })
    expect(((fetch.mock.calls[1]?.[1] as RequestInit).headers as Record<string, string>)['Idempotency-Key']).toBe(original.key)
  })
  it('retains the original key when authorization is revoked during verification', async () => {
    const fetch = vi.fn().mockRejectedValueOnce(new Error('lost response'))
      .mockResolvedValueOnce(Response.json({ code: 'PERMISSION_DENIED', traceId: 'fixture:denied' }, { status: 403 }))
    vi.stubGlobal('fetch', fetch)
    const mutation = useMutation()
    await mutation.perform('/fixture', 'POST', { reason: 'original' }, 'csrf')
    const original = mutation.pending.value
    await mutation.perform('/different', 'PATCH', {}, 'csrf')
    expect(mutation.pending.value).toBe(original); expect(mutation.uncertain.value).toBe(true)
    expect(mutation.error.value).toContain('无法核实原操作'); expect(mutation.errorCode.value).toBe('PERMISSION_DENIED')
    expect(mutation.traceId.value).toBe('fixture:denied'); expect(fetch).toHaveBeenCalledTimes(2)
  })
  it('clears all local tracking on dispose and ignores a late response', async () => {
    vi.useFakeTimers()
    let resolve!: (response: Response) => void
    const fetch = vi.fn(() => new Promise<Response>(finish => { resolve = finish })); vi.stubGlobal('fetch', fetch)
    const mutation = useMutation(), submission = mutation.perform('/fixture', 'POST', { password: 'fixture secret' }, 'csrf')
    mutation.dispose(); resolve(Response.json({ id: 'late' })); await submission
    expect(mutation.pending.value).toBeNull(); expect(mutation.busy.value).toBe(false)
    expect(mutation.error.value).toBe(''); expect(mutation.operationId.value).toBe(''); expect(mutation.traceId.value).toBe('')
    expect(vi.getTimerCount()).toBe(0)
    await mutation.perform('/fixture', 'POST', {}, 'csrf'); expect(fetch).toHaveBeenCalledTimes(1)
  })
  it('renders only safe operation metadata, never the secret request snapshot', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ code: 'DEPENDENCY_UNAVAILABLE', traceId: 'fixture:trace' }, { status: 503 })))
    const mutation = useMutation()
    await mutation.perform('/fixture', 'POST', { temporaryPassword: 'memory-only fixture secret' }, 'csrf')
    const root = document.createElement('div'); document.body.appendChild(root)
    const app = createApp(OperationStatus, { mutation }); app.mount(root); await nextTick()
    try {
      expect(root.textContent).toContain(mutation.operationId.value); expect(root.textContent).toContain('fixture:trace')
      expect(root.textContent).not.toContain('memory-only fixture secret'); expect(root.textContent).not.toContain('temporaryPassword')
      expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0)
    } finally { app.unmount(); mutation.dispose() }
  })
})
