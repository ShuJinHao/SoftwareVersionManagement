import type { Permission } from './api'

export interface AccessScope { kind: 'Site' | 'Process' | 'Device' | 'Software'; resourceId: string | null; name?: string | null }
export interface Role { id: string; name: string; isEnabled: boolean; revision: number; operations: string[]; assignedPeople: number }
export interface RoleAssignment { roleId: string; name: string; isEnabled: boolean; revision: number; operations: string[]; scopes: AccessScope[] }
export interface Authorization { subjectId: string; revision: number; directPermissions: Permission[]; roles: RoleAssignment[]; softwareNames?: Record<string,string> }
export interface AssignmentInput { roleId: string; scopes: AccessScope[] }
export const fieldOperations = ['asset.read','asset.create','asset.manage','software.create','binding.manage','software.read','instance.read','instance.manage','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','audit.read']
export const productOperations = ['software.read','release.source.manage','release.upload','release.publish','release.disable','audit.read']
export const operationGroups = [
  { name:'平台管理', operations:['identity.manage'] },
  { name:'现场职责', operations:fieldOperations },
  { name:'产品管理（须另选具体软件）', operations:['release.source.manage','release.upload','release.publish','release.disable'] },
]
export const operationLabels:Record<string,string> = {
  'identity.manage':'管理人员、角色与授权','asset.read':'查看现场台账','asset.create':'创建工序、设备和硬件','asset.manage':'修改现场台账',
  'software.create':'创建软件','binding.manage':'维护软件关联','software.read':'查看版本与下载','instance.read':'查看安装与状态','instance.manage':'管理实例接入状态',
  'enrollment.manage':'管理登记许可与凭据','deployment.create':'创建更新','deployment.control':'控制更新','task.closeUnknown':'人工关闭未知结果',
  'release.source.manage':'管理 GitLab 来源','release.upload':'维护产品、上传与同步','release.publish':'发布正式版','release.disable':'停用版本','audit.read':'查看审计',
}
export const label = (op:string) => operationLabels[op] ?? op
export const scopeLabel = (s:AccessScope) => s.kind==='Site' ? '本厂全部现场' : `${({Process:'工序',Device:'设备',Software:'产品'} as Record<string,string>)[s.kind]} · ${s.name ?? '已选对象（名称待读取）'}`
const active = (a:Authorization,op:string) => a.roles.filter(r=>r.isEnabled && r.operations.includes(op))
export function canGlobal(a:Authorization,op:string) {
  return a.directPermissions.some(p=>p.softwareId===null && (p.operation===op || op==='asset.create' && p.operation==='asset.manage')) || active(a,op).some(r=>r.scopes.some(s=>s.kind==='Site'))
}
export function canProduct(a:Authorization,op:string,softwareId:string) {
  return a.directPermissions.some(p=>p.softwareId===softwareId && p.operation===op) || productOperations.includes(op) && active(a,op).some(r=>r.scopes.some(s=>s.kind==='Software' && s.resourceId===softwareId))
}
export function canAnyField(a:Authorization,op:string) {
  return a.directPermissions.some(p=>p.operation===op || op==='asset.create' && p.softwareId===null && p.operation==='asset.manage') || active(a,op).some(r=>r.scopes.some(s=>['Site','Process','Device'].includes(s.kind)))
}
export function canAt(a:Authorization,op:string,softwareId:string|null=null,processId:string|null=null,deviceId:string|null=null) {
  if(op==='asset.create' && a.directPermissions.some(p=>p.softwareId===null && p.operation==='asset.manage')) return true
  if(op==='binding.manage' && a.directPermissions.some(p=>p.softwareId===null && p.operation==='asset.manage') && a.directPermissions.some(p=>p.softwareId===softwareId && p.operation==='instance.manage')) return true
  if(a.directPermissions.some(p=>p.operation===op && (p.softwareId===null || p.softwareId===softwareId))) return true
  return fieldOperations.includes(op) && active(a,op).some(r=>r.scopes.some(s=>s.kind==='Site' || s.kind==='Process' && s.resourceId===processId || s.kind==='Device' && s.resourceId===deviceId))
}
