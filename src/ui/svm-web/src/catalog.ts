export interface Site { siteId: string; siteName: string; siteTimeZone: string }
export interface Process { id: string; siteId: string; code: string; name: string; revision: number }
export interface Device { id: string; deviceNo: string; name: string; processId: string; processCode: string; processName: string; siteId: string; siteName: string; revision: number }
export interface Software { id: string; code: string; name: string; category: 'UpperComputer' | 'Vision'; description: string | null; latestAvailableFormalReleaseId: null; revision: number }
export interface Binding { deviceId: string; softwareId: string; revision: number }
export interface Inventory { software: Software; binding: Binding; instance: import('./instances').Instance | null }
export interface Page<T> { items: T[]; nextCursor: string | null; serverTime: string }
export interface PermissionOptions extends Page<Pick<Software, 'id' | 'code' | 'name' | 'category'>> { softwareOperations: string[] }
export const categoryName = (value: string) => value === 'UpperComputer' ? '上位机' : '视觉'
export const operationNames: Record<string, string> = { 'software.read': '查看软件', 'instance.read': '查看实例', 'instance.manage': '维护实例与设备映射',
  'release.upload': '维护软件资料及上传版本', 'release.publish': '转正式', 'release.disable': '停用版本', 'enrollment.manage': '维护接入授权',
  'deployment.create': '发起投放', 'deployment.control': '控制投放', 'deployment.rollback': '回退', 'task.closeUnknown': '关闭未知结果跟踪', 'package.clean': '清理安装包', 'audit.read': '查看软件审计' }
