import type { Device, Software, Page } from './catalog'
import type { Instance } from './instances'

export interface Hardware { id: string; deviceId: string; type: 'IPC' | 'PLC'; name: string; assetNumber: string | null; managementIp: string | null; revision: number }
export interface Position { id: string; hardwareId: string; deviceId: string; softwareId: string; isActive: boolean; instanceId: string | null; revision: number }
export interface WorkspaceDevice { device: Device; hardwareCount: number; installationCount: number; pendingLocationCount: number; neverReportedCount: number; unknownCount: number }
export interface Installation { rowId: string; device: Device; hardware: Hardware | null; position: Position | null; software: Software; instance: Instance | null; locationState: 'PendingHardware' | 'NotRegistered' | 'Registered' | 'PendingConfirmation' | 'Confirmed'; plcUsage?: import('./plc').PlcUsageSummary | null; latestFormalVersion?: string | null }
export interface WorkspacePage<T> extends Page<T> { totalCount: number }
export function mergeRows<T>(previous: T[], next: T[], id: (x: T) => string) { return [...new Map([...previous, ...next].map(x => [id(x), x])).values()] }
export function queryString(values: Record<string, string | null | undefined>, cursor?: string | null) {
  const query = new URLSearchParams({ pageSize: '50' })
  for (const [key, value] of Object.entries(values)) if (value) query.set(key, value)
  if (cursor) query.set('cursor', cursor)
  return query.toString()
}
