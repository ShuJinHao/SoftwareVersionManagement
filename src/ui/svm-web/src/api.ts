export interface Permission { softwareId: string | null; operation: string }
export interface Session {
  authenticated: boolean; subjectId: string | null; employeeNo: string | null; displayName: string | null
  mustChangePassword: boolean | null; permissions: Permission[]; csrfToken: string; serverTime: string
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
  INVALID_REQUEST: '请求格式不正确，请重新加载后再试。', REVISION_REQUIRED: '请先加载账号的最新资料。',
  REVISION_CONFLICT: '资料已被修改，请重新加载详情并确认。',
  INVALID_STATE: '操作不符合当前条件：请检查编号是否重复、映射是否已被引用，并保留至少一个有效管理员。',
  IDEMPOTENCY_CONFLICT: '原操作信息发生冲突，请核实该操作。', RESOURCE_NOT_FOUND: '对象不存在或不可访问。',
  RATE_LIMITED: '操作过于频繁，请稍后再试。', PAYLOAD_TOO_LARGE: '提交内容过大，请减少内容。',
  DEPENDENCY_UNAVAILABLE: '服务暂不可用，操作结果需要核实。', CONFIGURATION_INVALID: '服务配置不可用，请联系管理员。',
  REGISTRATION_CONFLICT: '登记资料与已有安装不一致，请通过受控恢复核实身份。', REPORT_CONFLICT: '当前报告序号的内容冲突。', INSTANCE_SUSPENDED: '此实例的 API 接入已暂停。', GRANT_EXPIRED: '登记许可已到期。', GRANT_EXHAUSTED: '登记许可名额已耗尽。', GRANT_REVOKED: '登记许可已撤销。',
  CONNECTION_UNKNOWN: '连接中断，操作结果尚未确认。',
}
export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, public readonly traceId?: string) {
    super(messages[code] ?? '请求未完成，请稍后检查。')
  }
  get uncertain() { return this.status === 0 || this.status >= 500 || this.code === 'IDEMPOTENCY_CONFLICT' }
}
export async function request<T>(path: string, method = 'GET', body?: unknown, csrfToken?: string, key?: string): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store',
      headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(csrfToken ? { 'X-CSRF-TOKEN': csrfToken } : {}), ...(key ? { 'Idempotency-Key': key } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch { throw new ApiError(0, 'CONNECTION_UNKNOWN') }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { code?: string; traceId?: string }
    throw new ApiError(response.status, problem.code ?? 'DEPENDENCY_UNAVAILABLE', problem.traceId)
  }
  if (response.status === 204) return undefined as T
  try { return await response.json() as T } catch { throw new ApiError(0, 'CONNECTION_UNKNOWN') }
}
