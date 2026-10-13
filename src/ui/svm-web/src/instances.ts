export interface DatabaseState { mode: 'Unknown' | 'None' | 'Present'; items: { databaseKey: string; schemaId: string }[] }
export interface StateReport { streamEpoch: number; reportSeq: number; reportedAt: string; installationState: 'NotInstalled' | 'Installed' | 'Unknown'; installedReleaseId: string | null; installedVersion: string | null; installedAt: string | null; runningState: 'Running' | 'Stopped' | 'Unknown'; reportedIps: string[]; databaseState: DatabaseState }
export interface Instance { id: string; softwareId: string; deviceId: string; deviceNo: string; deviceName: string; location: { siteId: string; siteName: string; processId: string; processCode: string; processName: string }; lifecycle: 'Active' | 'Suspended'; lastSnapshot: StateReport | null; lastAcceptedAt: string | null; unreportedSeconds: number | null; freshness: 'NeverReported' | 'Fresh' | 'Unknown'; latestAvailableFormalReleaseId: string | null; latestTaskId: string | null; latestTaskResult: string | null; revision: number }
export interface Grant { id: string; softwareId: string; deviceIds: string[] | null; instanceId: string | null; state: 'Active' | 'Expired' | 'Exhausted' | 'Revoked' | 'Consumed'; expiresAt: string; maxInstances: number; usedCount: number; revision: number }
export interface Credential { id: string; subjectId: string; expiresAt: string | null; revokedAt: string | null; revision: number }
export interface History { id: string; instanceId: string; installationState: string; installedReleaseId: string | null; installedVersion: string | null; installedAt: string | null; receivedAt: string; reportedRunningState: string }
export const freshnessName = (x: string) => ({ NeverReported: '尚未上报', Fresh: '正常上报', Unknown: '状态未知' }[x] ?? x)
export const runningName = (x?: string) => ({ Running: '运行', Stopped: '停止', Unknown: '未知' }[x ?? 'Unknown'] ?? x)
export const installationName = (x?: string) => ({ Installed: '已安装', NotInstalled: '未安装', Unknown: '安装状态未知' }[x ?? 'Unknown'] ?? x)
export const grantStateName = (x: string) => ({ Active: '有效', Expired: '已到期', Exhausted: '名额耗尽', Revoked: '已撤销', Consumed: '已使用' }[x] ?? x)
export function generateSecret() { return btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32)))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') }

export function formatTime(value: string, timeZone = 'UTC') {
  return new Intl.DateTimeFormat('zh-CN', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).format(new Date(value))
}
