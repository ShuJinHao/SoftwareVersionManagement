import { computed, shallowRef, ref, type InjectionKey, type Ref } from 'vue'
import { ApiError, operationError, request } from './api'

export interface Pending { readonly path: string; readonly method: string; readonly body: unknown; readonly key: string }
export const pendingWriteKey: InjectionKey<Ref<boolean>> = Symbol('pending-personnel-write')
function freezeJson(value: unknown): unknown {
  if (value && typeof value === 'object') {
    for (const item of Object.values(value)) freezeJson(item)
    Object.freeze(value)
  }
  return value
}
export function useMutation() {
  const pending = shallowRef<Pending | null>(null)
  const busy = ref(false)
  const error = ref('')
  const operationId = ref(''), traceId = ref(''), errorCode = ref('')
  let disposed = false, active: AbortController | null = null
  const uncertain = computed(() => pending.value !== null && !busy.value)
  async function perform<T>(path: string, method: string, body: unknown, csrf: string): Promise<T | undefined> {
    if (disposed || busy.value) return undefined
    const recovering = pending.value !== null
    if (pending.value === null) {
      try {
        const snapshot = body === undefined ? undefined : JSON.parse(JSON.stringify(body)) as unknown
        const key = crypto.randomUUID()
        pending.value = Object.freeze({ path, method, body: freezeJson(snapshot), key })
        operationId.value = key; traceId.value = ''
      } catch { error.value = '请求内容无法序列化，请检查输入。'; return undefined }
    }
    const operation = pending.value
    const controller = new AbortController(); active = controller
    busy.value = true; error.value = ''; errorCode.value = ''
    try {
      const result = await request<T>(operation.path, operation.method, operation.body, csrf, operation.key, { signal: controller.signal })
      if (disposed) return undefined
      pending.value = null
      return result
    } catch (e) {
      if (disposed) return undefined
      error.value = e instanceof ApiError ? operationError(e, operation.path) : '操作未完成，结果需要核实。'
      errorCode.value = e instanceof ApiError ? e.code : 'CONNECTION_UNKNOWN'
      if (e instanceof ApiError && e.traceId) traceId.value = e.traceId
      // A rejected verification cannot prove that the earlier uncertain request rolled back.
      if (e instanceof ApiError && !e.uncertain && !recovering) pending.value = null
      if (recovering && e instanceof ApiError && (e.status === 401 || e.status === 403))
        error.value = '当前会话或权限无法核实原操作。请保留此页，在其他窗口重新登录或联系管理员后，再核实原操作。'
      return undefined
    } finally { if (active === controller) active = null; if (!disposed) busy.value = false }
  }
  // A page/unmount ends only local tracking; it does not claim the server operation failed.
  function dispose() { disposed = true; active?.abort(); active = null; pending.value = null; busy.value = false; error.value = ''; operationId.value = ''; traceId.value = ''; errorCode.value = '' }
  return { perform, pending, busy, uncertain, error, errorCode, operationId, traceId, dispose }
}
