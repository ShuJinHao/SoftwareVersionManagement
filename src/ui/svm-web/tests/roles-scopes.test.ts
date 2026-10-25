import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, type Component } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { canAt, canGlobal, canProduct, type Authorization, type RoleAssignment } from '../src/authorization'
import { useSession } from '../src/session'
import UsersPage from '../src/pages/UsersPage.vue'
import RolesPage from '../src/pages/RolesPage.vue'
import DeploymentsPage from '../src/pages/DeploymentsPage.vue'

const role=(operations:string[], kind:'Site'|'Device'|'Process'|'Software', resourceId:string|null):RoleAssignment=>({roleId:operations.join('-'),name:'虚构职责',isEnabled:true,revision:1,operations,scopes:[{kind,resourceId,name:'虚构范围'}]})
const auth=(roles:RoleAssignment[]):Authorization=>({subjectId:'admin',revision:1,directPermissions:[],roles})
const account=(authorization=auth([role(['identity.manage'],'Site',null)]))=>({authenticated:true,subjectId:'admin',employeeNo:'FIXTURE',displayName:'虚构管理员',mustChangePassword:false,permissions:[],authorization,authorizationRevision:authorization.revision,csrfToken:'fixture',serverTime:'2026-10-24T00:00:00Z'})
const page=(items:unknown[],nextCursor:string|null=null)=>Response.json({items,nextCursor,serverTime:'2026-10-24T00:00:00Z'})
const disposers:(()=>void)[]=[]
afterEach(()=>{disposers.splice(0).forEach(x=>x());vi.restoreAllMocks();vi.unstubAllGlobals();document.body.innerHTML='';localStorage.clear();sessionStorage.clear()})
async function mount(component:Component, path='/system', initial=account()) {
  vi.spyOn(HTMLDialogElement.prototype,'showModal').mockImplementation(function(){this.open=true})
  vi.spyOn(HTMLDialogElement.prototype,'close').mockImplementation(function(){this.open=false})
  const router=createRouter({history:createMemoryHistory(),routes:[{path:path.split('?')[0]!,component}]});await router.push(path)
  const pinia=createPinia(),session=useSession(pinia);session.current=initial
  const root=document.createElement('div');document.body.appendChild(root)
  const app=createApp({template:'<RouterView />'}).use(pinia).use(router);app.mount(root);disposers.push(()=>app.unmount())
  return {root,session}
}
async function click(text:string,root:ParentNode=document){const b=[...root.querySelectorAll('button')].find(x=>x.textContent?.trim()===text);expect(b).toBeTruthy();b!.click();await nextTick()}
async function settle(){for(let i=0;i<40;i++){await nextTick();await Promise.resolve()}}
function fill(selector:string,value:string){const input=document.querySelector(selector) as HTMLInputElement;input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}))}

describe('roles and site scopes',()=>{
 it('keeps each operation paired with its own scope and separates explicit product rights',()=>{
  const a=auth([role(['asset.read','deployment.create'],'Process','P-A'),role(['asset.manage'],'Device','D-B'),role(['release.publish'],'Site',null)])
  expect(canAt(a,'deployment.create','shared','P-A','D-A')).toBe(true)
  expect(canAt(a,'deployment.create','shared','P-B','D-B')).toBe(false)
  expect(canAt(a,'asset.manage',null,'P-A','D-A')).toBe(false)
  expect(canAt(a,'asset.manage',null,'P-B','D-B')).toBe(true)
  expect(canProduct(a,'release.publish','shared')).toBe(false)
  a.roles.push(role(['release.publish'],'Software','shared'))
  expect(canProduct(a,'release.publish','shared')).toBe(true)
  expect(canProduct(a,'release.publish','other')).toBe(false)
  expect(canAt(a,'release.publish','shared','P-A','D-A')).toBe(false)
 })
 it('preserves legacy direct grants without granting role names special authority',()=>{
  const a=auth([{...role([],'Site',null),name:'平台管理员'}])
  expect(canGlobal(a,'identity.manage')).toBe(false)
  a.directPermissions=[{softwareId:null,operation:'asset.manage'},{softwareId:'S',operation:'instance.manage'}]
  expect(canAt(a,'asset.create',null,null,'D')).toBe(true)
  expect(canAt(a,'binding.manage','S',null,'D')).toBe(true)
  expect(canAt(a,'binding.manage','other',null,'D')).toBe(false)
  a.roles.push({...role(['identity.manage'],'Site',null),isEnabled:false})
  expect(canGlobal(a,'identity.manage')).toBe(false)
 })
 it('shows role administrators management and leaves product-only readers in the software workspace',()=>{
  const session=useSession(createPinia());session.current=account();expect(session.canManage).toBe(true)
  session.current=account(auth([role(['software.read'],'Software','S')]))
  expect(session.canSoftware).toBe(true);expect(session.canAssets).toBe(false);expect(session.canManage).toBe(false)
  expect(session.can('release.publish','S')).toBe(false)
 })
 it('freezes the person and invalidates the authorization form after a range revision changes',async()=>{
  const user={id:'person-A',displayName:'人员 A',employeeNo:'A',isEnabled:true,mustChangePassword:false,permissions:[],revision:7}
  const authorization={...auth([role(['instance.read'],'Device','D-A')]),subjectId:user.id,revision:7}
  const fetch=vi.fn(async(path:string)=>path.includes('/permission-options')?Response.json({items:[],roles:[],devices:[],processes:[],nextCursor:null}):path.endsWith('/authorization')?Response.json(authorization):path.endsWith('/'+user.id)?Response.json(user):page([user]))
  vi.stubGlobal('fetch',fetch);const {root,session}=await mount(UsersPage)
  await vi.waitFor(()=>expect(root.textContent).toContain('人员 A'));await click('人员 A');await settle();await click('分配角色与范围');await settle()
  expect(root.querySelector('dialog')?.textContent).toContain('人员 A');expect(root.querySelector('dialog')?.textContent).toContain('虚构范围')
  session.current=account({...session.access,revision:2});await settle()
  expect(root.querySelector('dialog')?.textContent).toContain('当前身份或权限已变化')
  expect((root.querySelector('dialog fieldset') as HTMLFieldSetElement).disabled).toBe(true)
  root.querySelector('dialog form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await settle()
  expect(fetch.mock.calls.every(([path])=>!path.includes('/person-B/'))).toBe(true)
 })
 it('shows affected people and verifies a lost role write with the original key and body after revocation',async()=>{
  const item={id:'R',name:'现场维护',isEnabled:true,operations:['asset.read'],assignedPeople:12,revision:3}
  let writes=0,latest=account()
  const fetch=vi.fn(async(path:string,options?:RequestInit)=>{
    if(options?.method==='PATCH'){writes++;if(writes===1)throw new Error('lost response');return Response.json({code:'PERMISSION_DENIED'},{status:403})}
    if(path==='/api/v1/session')return Response.json(latest)
    return path.endsWith('/R')?Response.json(item):page([item])
  });vi.stubGlobal('fetch',fetch);const {root,session}=await mount(RolesPage)
  await vi.waitFor(()=>expect(root.textContent).toContain('现场维护'));await click('编辑');await settle()
  expect(root.querySelector('dialog')?.textContent).toContain('12 名')
  fill('dialog textarea','职责核对');root.querySelector('dialog form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await settle()
  expect(writes).toBe(1);expect(root.textContent).toContain('核实原操作')
  latest=account({...auth([]),revision:2});session.current=latest;await settle()
  await click('核实原操作');await settle();expect(writes).toBe(2)
  const calls=fetch.mock.calls.filter(([,o])=>o?.method==='PATCH');expect(calls[0]![1]!.body).toEqual(calls[1]![1]!.body)
  expect((calls[0]![1]!.headers as Record<string,string>)['Idempotency-Key']).toBe((calls[1]![1]!.headers as Record<string,string>)['Idempotency-Key'])
  expect(root.textContent).toContain('无法核实');expect(localStorage.length+sessionStorage.length).toBe(0)
 })
 it('rejects late deployment details when only role range changes and hides whole controls without whole scope',async()=>{
  const a=account(auth([role(['instance.read','deployment.control'],'Process','P-A')]))
  const d={id:'D',softwareId:'S',targetReleaseId:'V',state:'Active',window:{notBefore:'2026-10-24T00:00:00Z',latestStart:'2026-10-25T00:00:00Z'},resultCounts:{succeeded:0,failed:0,canceled:0,closedUnknown:0,unfinished:1},pauseReasons:[],revision:1,effectiveOperations:[]}
  let finish!:(r:Response)=>void;const late=new Promise<Response>(r=>{finish=r})
  vi.stubGlobal('fetch',vi.fn(async(path:string)=>path.endsWith('/D/tasks')?late:path.endsWith('/D')?Response.json(d):path.includes('/deployments?')?page([d]):page([])))
  const {root,session}=await mount(DeploymentsPage,'/deployments?softwareId=S',a);await settle();await click('D');await settle()
  session.current=account({...auth([role(['instance.read','deployment.control'],'Process','P-B')]),revision:2});await settle()
  finish(page([{id:'old-private-task'}]));await settle()
  expect(root.textContent).not.toContain('old-private-task');expect(root.textContent).not.toContain('投放详情')
  expect(root.textContent).toContain('负责范围已变化');expect([...root.querySelectorAll('button')].some(b=>b.textContent==='取消未获许可任务')).toBe(false)
 })
})
