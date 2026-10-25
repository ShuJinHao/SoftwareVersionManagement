export interface Permission { softwareId: string | null; operation: string }
export interface Session {
  authenticated: boolean; subjectId: string | null; employeeNo: string | null; displayName: string | null
  mustChangePassword: boolean | null; permissions: Permission[]; csrfToken: string; serverTime: string
  authorization?: import('./authorization').Authorization | null; authorizationRevision?: number
}
export interface User {
  id: string; employeeNo: string; displayName: string; isEnabled: boolean; mustChangePassword: boolean
  permissions: Permission[]; revision: number
}
export interface UserPage { items: User[]; nextCursor: string | null; serverTime: string }
const messages: Record<string, string> = {
  AUTHENTICATION_REQUIRED: '会话已失效，请重新登录。', CREDENTIAL_INVALID: '工号或密码不正确。',
  PERMISSION_DENIED: '当前账号无权执行此操作，请检查权限或重新登录。',
  VALIDATION_FAILED: '请检查输入内容、密码要求及授权范围。', UNKNOWN_FIELD: '请求字段不受支持。',
  INVALID_REQUEST: '请求格式不正确，请重新加载后再试。', REVISION_REQUIRED: '请先加载对象的最新资料。',
  REVISION_CONFLICT: '资料已被修改，请重新加载详情并确认。',
  INVALID_STATE: '当前状态不允许此操作，请重新读取对象并核实操作条件。',
  IDEMPOTENCY_CONFLICT: '原操作信息发生冲突，请核实该操作。', RESOURCE_NOT_FOUND: '对象不存在或不可访问。',
  RATE_LIMITED: '操作过于频繁，请稍后再试。', PAYLOAD_TOO_LARGE: '提交内容过大，请减少内容。',
  DEPENDENCY_UNAVAILABLE: '服务暂不可用，操作结果需要核实。', CONFIGURATION_INVALID: '服务配置不可用，请联系管理员。',
  REGISTRATION_CONFLICT: '登记资料与已有安装不一致，请通过受控恢复核实身份。', REPORT_CONFLICT: '当前报告序号的内容冲突。', INSTANCE_SUSPENDED: '此实例的 API 接入已暂停。', GRANT_EXPIRED: '登记许可已到期。', GRANT_EXHAUSTED: '登记许可名额已耗尽。', GRANT_REVOKED: '登记许可已撤销。',
  CONNECTION_UNKNOWN: '连接中断，操作结果尚未确认。',
  REQUEST_TIMEOUT: '请求超时。读取可重新发起；写入结果尚未确认，请核实原操作。',
  REQUEST_CANCELLED: '读取已取消。',
  VERSION_CONFLICT: '版本或来源事实发生冲突，请核实版本编号、Tag 和已登记内容。',
  UPLOAD_IN_PROGRESS: '安装包仍在接收，请先查询原接收结果。',
  CHECKSUM_MISMATCH: '安装包摘要不一致，请核实原文件后再操作。',
  NO_HEALTHY_REPLICA: '当前没有可用的健康副本，请等待副本恢复后重新查询。',
  RECEIPT_CONFLICT: '同一回执标识或序号的内容冲突，请核实原回执。',
  ROLLBACK_REQUIRED: '目标版本低于当前安装版本，需要回退流程；当前入口只支持更新。',
  CURRENT_VERSION_UNKNOWN: '无法判断当前版本方向，请先核实实例安装事实。',
  RETRY_SOURCE_INVALID: '重试来源不符合条件，请重新核实原投放及任务结果。',
  HUMAN_REQUIRED: '此操作需要有权限的人员执行。',
}
export function safeTraceId(value: unknown): string | undefined {
  return typeof value === 'string' && /^[A-Za-z0-9._:-]{1,128}$/.test(value) ? value : undefined
}
export class ApiError extends Error {
  readonly traceId?: string
  constructor(public readonly status: number, public readonly code: string, traceId?: string) {
    super(messages[code] ?? '请求未完成，请稍后检查。')
    this.traceId = safeTraceId(traceId)
  }
  get cancelled() { return this.code === 'REQUEST_CANCELLED' }
  get uncertain() { return !this.cancelled && (this.status === 0 || this.status >= 500 || this.code === 'IDEMPOTENCY_CONFLICT') }
}
export interface RequestOptions { signal?: AbortSignal; timeoutMs?: number }
export function operationError(error: ApiError, path: string): string {
  if (error.code !== 'INVALID_STATE') return error.message
  if (/\/(users|subjects)(\/|$)/.test(path)) return '账号操作不符合当前条件。请核实工号唯一、账号状态，并保留至少一个启用的人员管理员。'
  if (path.endsWith('/publish')) return '暂不能转正式，请核实测试版本、匹配的安装证据和两个有效健康副本。'
  if (path.includes('/release-sync')) return '当前来源或同步工作状态不允许此操作，请核实 GitLab 绑定和同步结果。'
  if (/\/(deployments|tasks|target-selections)(\/|$)/.test(path)) return '任务操作不符合当前状态。请刷新投放、关联批次及逐台任务，核实许可、时段和控制条件。'
  return error.message
}
export async function request<T>(path: string, method = 'GET', body?: unknown, csrfToken?: string, key?: string, options: RequestOptions = {}): Promise<T> {
  const reading = ['GET', 'HEAD'].includes(method.toUpperCase())
  const timeoutMs = options.timeoutMs ?? (reading ? 15_000 : 30_000)
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) throw new ApiError(400, 'INVALID_REQUEST')
  let serialized: string | undefined
  try { serialized = body === undefined ? undefined : JSON.stringify(body) }
  catch { throw new ApiError(400, 'INVALID_REQUEST') }
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  let responseTraceId: string | undefined
  let rejectCancellation!: (error: ApiError) => void
  const cancelled = new Promise<never>((_, reject) => { rejectCancellation = reject })
  const abort = (timeout: boolean) => {
    if (controller.signal.aborted) return
    rejectCancellation(new ApiError(0, timeout ? 'REQUEST_TIMEOUT' : reading ? 'REQUEST_CANCELLED' : 'CONNECTION_UNKNOWN', responseTraceId))
    controller.abort()
  }
  const externalAbort = () => abort(false)
  try {
    options.signal?.addEventListener('abort', externalAbort, { once: true })
    timer = setTimeout(() => abort(true), timeoutMs)
    if (options.signal?.aborted) abort(false)
    const operation = async () => {
      if (controller.signal.aborted) return await cancelled
      const response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
          ...(csrfToken ? { 'X-CSRF-TOKEN': csrfToken } : {}), ...(key ? { 'Idempotency-Key': key } : {}) },
        body: serialized,
      })
      responseTraceId = safeTraceId(response.headers.get('X-Trace-Id'))
      if (!response.ok) {
        const problem = await response.json().catch(() => ({})) as { code?: string; traceId?: string }
        throw new ApiError(response.status, typeof problem.code === 'string' ? problem.code : 'DEPENDENCY_UNAVAILABLE', problem.traceId ?? responseTraceId)
      }
      if (response.status === 204 || method.toUpperCase() === 'HEAD') return undefined as T
      return await response.json() as T
    }
    return await Promise.race([operation(), cancelled])
  } catch (error) {
    if (error instanceof ApiError) throw error
    throw new ApiError(0, 'CONNECTION_UNKNOWN', responseTraceId)
  } finally {
    clearTimeout(timer)
    options.signal?.removeEventListener('abort', externalAbort)
  }
}
