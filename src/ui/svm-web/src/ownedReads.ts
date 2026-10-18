import { computed, onUnmounted, ref, watch } from 'vue'
import { createReadScope } from './reading'
import { useSession } from './session'

// Transport cancellation and ownership checks serve different purposes. A late
// success or error is ignored even when the transport ignores its AbortSignal.
export function useOwnedReads(clear: () => void) {
  const session = useSession()
  const authority = computed(() => JSON.stringify([session.current?.subjectId, session.current?.authenticated,
    session.current?.mustChangePassword, session.current?.permissions.map(p => [p.softwareId, p.operation]).sort()]))
  const scopes = new Map<string, ReturnType<typeof createReadScope>>()
  const active = ref(new Map<string, symbol>())
  const epoch = ref(0)
  let disposed = false
  function invalidate(...names: string[]) {
    for (const name of names.length ? names : scopes.keys()) { scopes.get(name)?.invalidate(); active.value.delete(name) }
  }
  watch(authority, () => { epoch.value++; invalidate(); clear() }, { flush: 'sync' })
  onUnmounted(() => { disposed = true; invalidate() })
  async function run<T>(name: string, restart: boolean, action: (signal: AbortSignal) => Promise<T>,
    apply: (value: T) => void, failed: (error: unknown) => void | Promise<void>) {
    if (disposed) return false
    if (restart) invalidate(name)
    if (active.value.has(name)) return false
    let scope = scopes.get(name)
    if (!scope) { scope = createReadScope(); scopes.set(name, scope) }
    const read = scope.capture(), owner = Symbol(name), actor = authority.value
    active.value.set(name, owner)
    const current = () => !disposed && read.current() && actor === authority.value
    try { const value = await action(read.signal); if (current()) { apply(value); return true } }
    catch (error) { if (current()) await failed(error) }
    finally { read.finish(); if (active.value.get(name) === owner) active.value.delete(name) }
    return false
  }
  return { run, invalidate, epoch, busy: computed(() => active.value.size > 0), pending: (name: string) => active.value.has(name) }
}
