export interface PlcScope { bindingId: string; bindingRevision: number }
export interface PlcCredential { id: string; softwareId: string; name: string; scope: PlcScope[]; expiresAt: string; revokedAt: string | null; revision: number }
export interface PlcUsage { id: string; hardwareId: string; bindingId: string; bindingRevision: number; softwareId: string; releaseId: string; version: string; releaseState: string; previousReleaseId: string | null; fieldAt: string; recordedAt: string; employeeNo: string; note: string; materials: string; correctsId: string | null }
export interface PlcUsageSummary { revision: number; current: PlcUsage | null }
export interface PlcEvidence { hardwareId: string; bindingId: string; bindingRevision: number; fieldAt: string; verification: string; materials: string; employeeNo: string; recordedAt: string }
export function localTime(value: string) { const d = new Date(value); return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0,16) }
export function plcDownload(bindingId: string, packageId: string) { return `/api/v1/plc-bindings/${encodeURIComponent(bindingId)}/packages/${encodeURIComponent(packageId)}/content` }
