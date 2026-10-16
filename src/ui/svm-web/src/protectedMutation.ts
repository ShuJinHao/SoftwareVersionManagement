import { computed, inject, onMounted, onUnmounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { pendingWriteKey, useMutation } from './mutation'
import { useSession } from './session'

export function useProtectedMutation() {
  const mutation = useMutation(), pending = inject(pendingWriteKey, ref(false)), session = useSession()
  const verifying = ref(false)
  let originalSubjectId: string | null = null, disposed = false
  const busy = computed(() => mutation.busy.value || verifying.value)
  const blocked = computed(() => busy.value || mutation.uncertain.value)
  const completed = computed(() => !blocked.value && !mutation.error.value)
  function differentSubject() {
    mutation.error.value = '当前登录人员与原操作人员不一致。请使用原人员账号登录后，再核实原操作。'
    mutation.errorCode.value = 'OPERATION_SUBJECT_CHANGED'
  }
  async function perform<T>(path: string, method: string, body: unknown, csrf: string): Promise<T | undefined> {
    if (disposed || busy.value) return undefined
    if (mutation.pending.value) {
      if (session.current?.authenticated && session.current.subjectId !== originalSubjectId) { differentSubject(); return undefined }
      // Cookies may have changed in another tab; the cached page identity is insufficient.
      verifying.value = true
      try { await session.load() } finally { verifying.value = false }
      if (disposed) return undefined
      if (!session.current?.authenticated || session.error) {
        mutation.error.value = '当前会话无法核实原操作。请使用原人员账号重新登录后，再核实原操作。'
        mutation.errorCode.value = 'CURRENT_SESSION_UNKNOWN'
        return undefined
      }
      if (!originalSubjectId || session.current.subjectId !== originalSubjectId) {
        differentSubject()
        return undefined
      }
      csrf = session.current.csrfToken
    } else originalSubjectId = session.current?.subjectId ?? null
    const result = await mutation.perform<T>(path, method, body, csrf)
    if (!mutation.pending.value) originalSubjectId = null
    return result
  }
  watch(blocked, value => { pending.value = value }, { flush: 'sync' })
  onBeforeRouteLeave(() => { if (blocked.value) { mutation.error.value = '请先等待或核实原操作，再离开此页面。'; return false } })
  const unload = (event: BeforeUnloadEvent) => { if (blocked.value) { event.preventDefault(); event.returnValue = '' } }
  onMounted(() => window.addEventListener('beforeunload', unload))
  onUnmounted(() => { disposed = true; window.removeEventListener('beforeunload', unload); pending.value = false; originalSubjectId = null; mutation.dispose() })
  return { ...mutation, perform, busy, blocked, completed }
}
