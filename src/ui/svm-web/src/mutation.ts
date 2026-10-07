import { computed, shallowRef, ref, type InjectionKey, type Ref } from 'vue'
import { ApiError, request } from './api'

interface Pending { path: string; method: string; body: unknown; key: string }
export const pendingWriteKey: InjectionKey<Ref<boolean>> = Symbol('pending-personnel-write')
export function useMutation() {
  const pending = shallowRef<Pending | null>(null)
  const busy = ref(false)
  const error = ref('')
  const uncertain = computed(() => pending.value !== null && !busy.value)
  async function perform<T>(path: string, method: string, body: unknown, csrf: string): Promise<T | undefined> {
    if (busy.value) return undefined
    const recovering = pending.value !== null
    if (pending.value === null) pending.value = { path, method, body, key: crypto.randomUUID() }
    const operation = pending.value
    busy.value = true; error.value = ''
    try {
      const result = await request<T>(operation.path, operation.method, operation.body, csrf, operation.key)
      pending.value = null
      return result
    } catch (e) {
      error.value = e instanceof ApiError ? e.message : '操作未完成。'
      // A rejected verification cannot prove that the earlier uncertain request rolled back.
      if (e instanceof ApiError && !e.uncertain && !recovering) pending.value = null
      if (recovering && e instanceof ApiError && (e.status === 401 || e.status === 403))
        error.value = '当前会话或权限无法核实原操作。请保留此页，在其他窗口重新登录或联系管理员后，再核实原操作。'
      return undefined
    } finally { busy.value = false }
  }
  // A page/unmount ends only local tracking; it does not claim the server operation failed.
  function dispose() { if (!busy.value) pending.value = null }
  return { perform, pending, busy, uncertain, error, dispose }
}
