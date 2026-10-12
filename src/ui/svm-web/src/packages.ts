import { ApiError } from './api'

export interface Release {
  id: string; softwareId: string; version: string; state: 'Staging' | 'Test' | 'Formal' | 'Disabled'
  changeLevel: 'Patch' | 'Minor' | 'Major'; changeSummary: string; changeReason: string; packageId: string
  downloadAvailable: boolean; createdBy: string; createdAt: string; disabledAt: string | null; disableReason: string | null; revision: number
}
export interface Package {
  id: string; releaseId: string; state: 'Uploading' | 'Verifying' | 'Ready' | 'Failed'; sizeBytes: number | null; sha256: string | null
  expectedSize: number; expectedSha256: string; healthyReplicaCount: number; downloadAvailable: boolean; downloadPath: string | null
  processingStage: string; lastErrorCode: string | null; revision: number; uploadId: string; fileName: string
}
export interface ReleaseUpload { release: Release; uploadId: string; uploadPath: string }
export interface TestEvidence { id: string; instanceId: string; releaseId: string; installedVersion: string; installedAt: string | null; receivedAt: string; reportedRunningState: string }
export interface DownloadAudit { requestId: string; packageId: string; subjectId: string | null; actorKind: string; employeeNo: string | null; startedAt: string; endedAt: string | null; bytesSent: number | null; state: string }
const releaseStates: Record<string, string> = { Staging: '待接收 / 处理中', Test: '测试版', Formal: '正式版', Disabled: '已停用' }
const packageStages: Record<string, string> = { AwaitingUpload: '等待上传', Receiving: '正在接收', AwaitingReceipt: '等待后台接收', Copying: '校验并复制', Completed: '副本已核对', UploadFailed: '接收失败', CopyFailed: '副本处理失败', Stopped: '已停止' }
export const releaseState = (value: string) => releaseStates[value] ?? value
export const packageStage = (value: string) => packageStages[value] ?? value

// A raw upload retains its existing upload ID. This function never retries or allocates a release.
export async function uploadPackage(path: string, file: File, csrf: string, signal: AbortSignal): Promise<Package> {
  let response: Response
  try { response = await fetch(path, { method: 'PUT', credentials: 'same-origin', cache: 'no-store', signal,
    headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-TOKEN': csrf }, body: file }) }
  catch { throw new ApiError(0, 'CONNECTION_UNKNOWN') }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { code?: string; traceId?: string }
    throw new ApiError(response.status, problem.code ?? 'DEPENDENCY_UNAVAILABLE', problem.traceId)
  }
  try { return await response.json() as Package } catch { throw new ApiError(0, 'CONNECTION_UNKNOWN') }
}
