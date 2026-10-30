import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import WorkspacePage from '../src/pages/WorkspacePage.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
import { router as applicationRouter } from '../src/router'

const ids={subject:'11111111-1111-4111-8111-111111111111',process:'22222222-2222-4222-8222-222222222222',device:'33333333-3333-4333-8333-333333333333',hardware:'44444444-4444-4444-8444-444444444444',software:'55555555-5555-4555-8555-555555555555',position:'66666666-6666-4666-8666-666666666666',instance:'77777777-7777-4777-8777-777777777777'}
const person={authenticated:true,subjectId:ids.subject,employeeNo:'FIXTURE',displayName:'虚构操作员',mustChangePassword:false,permissions:[...['asset.read','asset.manage','software.create'].map(operation=>({softwareId:null,operation})),...['software.read','instance.read','instance.manage','enrollment.manage','deployment.create','release.upload'].map(operation=>({softwareId:ids.software,operation}))],csrfToken:'csrf',serverTime:'2026-10-22T00:00:00Z'}
const process={id:ids.process,name:'虚构工序',code:'PR-000001',revision:1}
const device={id:ids.device,processId:ids.process,processName:process.name,deviceNo:'EQ-000001',name:'虚构设备 A',revision:1}
const hardware={id:ids.hardware,deviceId:ids.device,type:'IPC',name:'工控机 A',managementIp:'192.0.2.10',revision:1}
const software={id:ids.software,name:'上位机 A',code:'SW-000001',category:'UpperComputer',revision:1}
const position={id:ids.position,hardwareId:ids.hardware,deviceId:ids.device,softwareId:ids.software,revision:1,isActive:true,instanceId:null}
const row={rowId:ids.position,device,hardware,software,position,instance:null,locationState:'NotRegistered'}
const page=(items:unknown[],nextCursor:string|null=null)=>({items,nextCursor,totalCount:items.length,serverTime:person.serverTime})
const disposals:(()=>void)[]=[]
afterEach(()=>{disposals.splice(0).forEach(d=>d());vi.restoreAllMocks();vi.unstubAllGlobals();document.body.innerHTML='';localStorage.clear();sessionStorage.clear()})
function fixture(path:string){const u=new URL(path,'https://fixture.invalid')
 if(path==='/api/v1/session')return person
 if(u.pathname.endsWith('/site'))return {siteId:ids.subject,siteName:'虚构厂区',siteTimeZone:'Asia/Shanghai'}
 if(u.pathname.endsWith('/processes'))return page([process])
 if(u.pathname.endsWith('/software'))return page([software])
 if(u.pathname.endsWith('/software/'+ids.software))return software
 if(u.pathname.endsWith('/devices/'+ids.device))return device
 if(u.pathname.endsWith('/hardware'))return page([hardware,{...hardware,id:'plc',type:'PLC',name:'PLC 主控'}])
 if(u.pathname.endsWith('/workspace/devices'))return page([{device,hardwareCount:2,installationCount:1,pendingLocationCount:0,neverReportedCount:0,unknownCount:0}])
 if(u.pathname.endsWith('/workspace/installations'))return page([row])
 if(u.pathname.endsWith('/deployment-capabilities'))return {timeZone:'Asia/Shanghai',defaultStartLocalTime:'02:00',defaultLatestStartLocalTime:'04:00',batchSize:2,failureLimit:1,resultWaitSeconds:30,selectionChunkSize:100}
 return page([])
}
async function mount(path='/workspace?deviceId='+ids.device){
 vi.spyOn(HTMLDialogElement.prototype,'showModal').mockImplementation(function(){this.open=true})
 vi.spyOn(HTMLDialogElement.prototype,'close').mockImplementation(function(){this.open=false})
 const router=createRouter({history:createMemoryHistory(),routes:[{path:'/workspace',component:WorkspacePage},{path:'/other',component:{template:'<p>other</p>'}}]});await router.push(path)
 const pinia=createPinia(),session=useSession(pinia);session.current=JSON.parse(JSON.stringify(person));const pending=ref(false)
 const root=document.createElement('div');document.body.appendChild(root);const app=createApp({template:'<RouterView />'}).use(pinia).use(router).provide(pendingWriteKey,pending);app.mount(root);disposals.push(()=>app.unmount());return {root,router,session,pending}
}
async function click(text:string,scope:ParentNode=document){const b=[...scope.querySelectorAll('button')].find(x=>x.textContent?.trim()===text);expect(b).toBeTruthy();b!.click();await nextTick()}
function fill(label:string,value:string){const root=document.querySelector('dialog[open]') ?? document;const l=[...root.querySelectorAll('label')].find(x=>x.textContent?.startsWith(label))!;expect(l).toBeTruthy();const i=l.querySelector('input,textarea,select') as HTMLInputElement;i.value=value;i.dispatchEvent(new Event(i.tagName==='SELECT'?'change':'input',{bubbles:true}))}
function input(name:string,value:string){const i=document.querySelector(`[aria-label="${name}"]`) as HTMLInputElement;i.value=value;i.dispatchEvent(new Event('input',{bubbles:true}))}
function deferred(){let resolve!:(value:Response)=>void;const promise=new Promise<Response>(r=>{resolve=r});return {promise,resolve}}

describe('hardware workspace',()=>{
 it('reloads the site after creating software changes current permissions',async()=>{
  let created=false
  const fetch=vi.fn(async(path:string,init?:RequestInit)=>{
   if(init?.method==='POST'){created=true;return Response.json({...position,id:'new-position'})}
   if(path==='/api/v1/session'&&created)return Response.json({...person,permissions:[...person.permissions,{softwareId:'new-software',operation:'software.read'}]})
   return Response.json(fixture(path))
  });vi.stubGlobal('fetch',fetch)
  const {root}=await mount();await vi.waitFor(()=>expect(root.textContent).toContain('工控机 A'))
  await click('关联软件');fill('软件来源','new');await nextTick();fill('软件名称','新视觉');fill('操作原因','指定工控机新建');await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(fetch.mock.calls.filter(c=>c[0]==='/api/v1/manage/site')).toHaveLength(2))
  await vi.waitFor(()=>expect(root.querySelector('.work-heading .eyebrow')?.textContent).toBe('虚构厂区'))
  expect(document.querySelector('dialog')!.open).toBe(false);expect(root.textContent).toContain('工控机 A')
 })
 it('groups IPC software and empty PLC program libraries without simulated state',async()=>{
  vi.stubGlobal('fetch',vi.fn(async(path:string)=>Response.json(fixture(path))));const {root}=await mount()
  await vi.waitFor(()=>expect(root.querySelectorAll('.hardware-section')).toHaveLength(2))
  expect(root.textContent).toContain('工控机 A');expect(root.textContent).toContain('上位机 A');expect(root.textContent).toContain('尚未登记')
  const plc=[...root.querySelectorAll('.hardware-section')].find(x=>x.textContent?.includes('PLC 主控'))!;expect(plc.textContent).toContain('尚未关联主程序库');expect(plc.querySelector('table')).not.toBeNull();expect(plc.textContent).not.toContain('在线')
 })
 it('creates device with automatic code and preserves a fixed device for hardware writes',async()=>{
  const fetch=vi.fn(async(path:string,init?:RequestInit)=>Response.json(init?.method==='POST'?{...hardware,id:'new'}:fixture(path)));vi.stubGlobal('fetch',fetch)
  const {root,router}=await mount();await vi.waitFor(()=>expect(root.textContent).toContain('工控机 A'));await click('添加硬件');fill('名称','新增工控机');fill('管理 IP','192.0.2.30')
  await router.push('/workspace?deviceId=other');expect(router.currentRoute.value.query.deviceId).toBe(ids.device)
  await click('确认',document.querySelector('dialog')!);await vi.waitFor(()=>expect(fetch.mock.calls.some(c=>c[1]?.method==='POST')).toBe(true))
  const [path,init]=fetch.mock.calls.find(c=>c[1]?.method==='POST')!;expect(path).toBe(`/api/v1/manage/devices/${ids.device}/hardware`);expect(JSON.parse(init!.body as string)).toEqual({name:'新增工控机',type:'IPC',managementIp:'192.0.2.30',assetNumber:null})
  await vi.waitFor(()=>expect(document.querySelector('dialog')!.open).toBe(false));await router.push('/workspace?processId='+ids.process);await vi.waitFor(()=>expect(root.textContent).toContain('创建设备'));await click('创建设备');fill('名称','自动编号设备');await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(fetch.mock.calls.filter(c=>c[1]?.method==='POST')).toHaveLength(2));const b=JSON.parse(fetch.mock.calls.filter(c=>c[1]?.method==='POST')[1]![1]!.body as string);expect(b.processId).toBe(ids.process);expect(b).not.toHaveProperty('deviceNo')
 })
 it('uses the applied software search for pagination until an explicit new query',async()=>{
  const fetch=vi.fn(async(path:string)=>Response.json(path.includes('/manage/software?')?page([software],path.includes('cursor=')?null:'page2'):fixture(path)));vi.stubGlobal('fetch',fetch)
  await mount('/workspace?view=software');await vi.waitFor(()=>expect(document.body.textContent).toContain('更多软件'))
  input('搜索软件','未应用输入');await click('更多软件');await vi.waitFor(()=>expect(fetch.mock.calls.some(c=>c[0].includes('cursor=page2'))).toBe(true))
  const query=new URL(fetch.mock.calls.find(c=>c[0].includes('cursor=page2'))![0],'https://fixture.invalid').searchParams;expect(query.has('name')).toBe(false)
  await vi.waitFor(()=>expect((document.querySelector('[aria-label="查询软件"]') as HTMLButtonElement).disabled).toBe(false)); (document.querySelector('[aria-label="查询软件"]') as HTMLButtonElement).click();await vi.waitFor(()=>expect(fetch.mock.calls.some(c=>new URL(c[0],'https://fixture.invalid').searchParams.get('name')==='未应用输入')).toBe(true))
 })
 it('ignores late device success and failure after switching scope',async()=>{
  const late=deferred();const fetch=vi.fn(async(path:string)=>{if(path.endsWith('/devices/'+ids.device))return late.promise;if(path.endsWith('/devices/B'))return Response.json({...device,id:'B',name:'设备 B'});if(path.includes('/devices/B/hardware'))return Response.json(page([]));if(path.includes('deviceId=B'))return Response.json(page([]));return Response.json(fixture(path))});vi.stubGlobal('fetch',fetch)
  const {router,root}=await mount();await vi.waitFor(()=>expect(fetch.mock.calls.some(c=>c[0].endsWith('/devices/'+ids.device))).toBe(true));await router.push('/workspace?deviceId=B');await vi.waitFor(()=>expect(root.querySelector('.content-heading')?.textContent).toContain('设备 B'))
  late.resolve(Response.json({code:'RESOURCE_NOT_FOUND',message:'旧设备失败'},{status:404}));await new Promise(r=>setTimeout(r,30));expect(root.querySelector('.content-heading')?.textContent).toContain('设备 B');expect(root.textContent).not.toContain('旧设备失败')
 })
 it('issues a single-position grant and clears its secret when the dialog closes',async()=>{
  const fetch=vi.fn(async(path:string,init?:RequestInit)=>Response.json(init?.method==='POST'?{id:'grant',hardwareBindingId:ids.position}:fixture(path)));vi.stubGlobal('fetch',fetch)
  await mount();await vi.waitFor(()=>expect(document.body.textContent).toContain('接入软件'));await click('接入软件');fill('有效期','2026-10-23T10:00');fill('操作原因','接入确认');await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(document.body.textContent).toContain('一次显示的许可秘密'));const init=fetch.mock.calls.find(c=>c[1]?.method==='POST')![1]!;const b=JSON.parse(init.body as string)
  expect(b.hardwareBindingId).toBe(ids.position);expect(b.hardwareBindingRevision).toBe(1);expect(b.deviceIds).toEqual([ids.device]);expect(b.maxInstances).toBe(1);expect(b.secretMaterial.length).toBeGreaterThan(40)
  await click('完成并清除秘密');expect(document.body.textContent).not.toContain('一次显示的许可秘密');expect(localStorage.length+sessionStorage.length).toBe(0)
 })
 it('keeps unknown writes in memory and blocks panel changes until original-key verification',async()=>{
  let writes=0;const fetch=vi.fn(async(path:string,init?:RequestInit)=>{if(init?.method==='POST'){if(++writes===1)throw new Error('lost response');return Response.json(hardware)}return Response.json(fixture(path))});vi.stubGlobal('fetch',fetch)
  const {router,pending}=await mount();await vi.waitFor(()=>expect(document.body.textContent).toContain('工控机 A'));await click('添加硬件');fill('名称','固定草稿');await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(pending.value).toBe(true));await vi.waitFor(()=>expect(document.body.textContent).toContain('核实原操作'));await router.push('/workspace?panel=releases&softwareId='+ids.software);expect(router.currentRoute.value.query.panel).toBeUndefined();await router.push('/other');expect(router.currentRoute.value.path).toBe('/workspace')
  await click('核实原操作');await vi.waitFor(()=>expect(writes).toBe(2));const calls=fetch.mock.calls.filter(c=>c[1]?.method==='POST');expect(calls[0]![1]!.body).toBe(calls[1]![1]!.body);expect(calls[0]![1]!.headers).toEqual(calls[1]![1]!.headers);expect(localStorage.length+sessionStorage.length).toBe(0)
 })
 it('invalidates an open draft after permission changes without retargeting it',async()=>{
  const fetch=vi.fn(async(path:string)=>Response.json(fixture(path)));vi.stubGlobal('fetch',fetch);const {session}=await mount();await vi.waitFor(()=>expect(document.body.textContent).toContain('工控机 A'));await click('添加硬件');fill('名称','草稿');session.current={...person,permissions:person.permissions.filter(p=>p.operation!=='asset.manage')};await nextTick();expect(document.querySelector('dialog')!.textContent).toContain('原草稿禁止继续');expect((document.querySelector('dialog fieldset') as HTMLFieldSetElement).disabled).toBe(true)
 })
 it('carries selected installation identities into deployment without reselecting machines',async()=>{
  const instance={id:ids.instance,softwareId:ids.software,deviceId:ids.device,deviceName:device.name,deviceNo:device.deviceNo,hardwareName:hardware.name,hardwareBindingId:ids.position,revision:1,lifecycle:'Active',freshness:'NeverReported',lastSnapshot:null}
  const fetch=vi.fn(async(path:string)=>Response.json(path.includes('/workspace/installations?')?page([{...row,instance,position:{...position,instanceId:ids.instance}}]):fixture(path)));vi.stubGlobal('fetch',fetch)
  const {root}=await mount();await vi.waitFor(()=>expect(root.querySelector('.installation-table input')).toBeTruthy());(root.querySelector('.installation-table input') as HTMLInputElement).click();await nextTick();await click('更新所选软件')
  await vi.waitFor(()=>expect(root.textContent).toContain('已带入工作台勾选的安装位置'));expect(root.textContent).toContain('虚构设备 A / 工控机 A');expect(root.textContent).not.toContain('目标方式');expect(fetch.mock.calls.some(c=>c[0].includes('/manage/instances?'))).toBe(false)
 })
})


describe('workspace navigation and final facts',()=>{
 it('opens software reverse lookup and returns to the originating device',async()=>{
  vi.stubGlobal('fetch',vi.fn(async(path:string)=>Response.json(fixture(path))));const {root,router}=await mount()
  await vi.waitFor(()=>expect(root.querySelector('.installation-table')).toBeTruthy());await click('上位机 A',root.querySelector('.installation-table')!)
  await vi.waitFor(()=>expect(router.currentRoute.value.query.view).toBe('software'));await vi.waitFor(()=>expect(root.querySelector('.content-heading')?.textContent).toContain('上位机 A'))
  expect(root.querySelector('.installation-table')?.textContent).toContain('虚构设备 A');expect(root.querySelector('.installation-table')?.textContent).toContain('工控机 A')
  await click('返回原现场');await vi.waitFor(()=>expect(router.currentRoute.value.query.deviceId).toBe(ids.device))
 })
 it('freezes software revision and identity in the software edit dialog',async()=>{
  const fetch=vi.fn(async(path:string,init?:RequestInit)=>Response.json(init?.method==='PATCH'?{...software,revision:2}:fixture(path)));vi.stubGlobal('fetch',fetch)
  const {router,root}=await mount('/workspace?view=software&softwareId='+ids.software);await vi.waitFor(()=>expect(root.textContent).toContain('软件资料'));await click('软件资料');fill('名称','变更名称');fill('操作原因','虚构变更')
  await router.push('/workspace?view=software&softwareId=other');expect(router.currentRoute.value.query.softwareId).toBe(ids.software);await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(fetch.mock.calls.some(x=>x[1]?.method==='PATCH')).toBe(true));const [path,options]=fetch.mock.calls.find(x=>x[1]?.method==='PATCH')!;expect(path).toBe('/api/v1/manage/software/'+ids.software);expect(JSON.parse(options!.body as string)).toEqual({name:'变更名称',description:null,expectedRevision:1,reason:'虚构变更'})
 })
 it('distinguishes reported NotInstalled from no report and displays current task facts',async()=>{
  const instance={id:ids.instance,softwareId:ids.software,lifecycle:'Active',freshness:'Fresh',lastSnapshot:{installationState:'NotInstalled',installedVersion:null,runningState:'Stopped',reportedIps:[]},latestTaskId:'task',latestTaskResult:null}
  vi.stubGlobal('fetch',vi.fn(async(path:string)=>Response.json(path.includes('/workspace/installations?')?page([{...row,instance}]):fixture(path))))
  const {root}=await mount();await vi.waitFor(()=>expect(root.querySelector('.installation-table')?.textContent).toContain('尚未安装'));expect(root.querySelector('.installation-table')?.textContent).not.toContain('尚未上报');expect(root.querySelector('.installation-table')?.textContent).toContain('查看进度')
 })
 it('verifies a fixed position grant using the original key and generated secret',async()=>{
  const writes:RequestInit[]=[];vi.stubGlobal('fetch',vi.fn(async(path:string,init?:RequestInit)=>{if(init?.method==='POST'){writes.push(init);if(writes.length===1)throw new Error('lost response');return Response.json({id:'grant',hardwareBindingId:ids.position})}return Response.json(fixture(path))}))
  const {pending}=await mount();await vi.waitFor(()=>expect(document.body.textContent).toContain('接入软件'));await click('接入软件');fill('有效期','2026-10-23T10:00');fill('操作原因','许可核实');await click('确认',document.querySelector('dialog')!)
  await vi.waitFor(()=>expect(pending.value).toBe(true));await vi.waitFor(()=>expect(document.body.textContent).toContain('核实原操作'));await click('核实原操作');await vi.waitFor(()=>expect(writes).toHaveLength(2));expect(writes[0]!.body).toBe(writes[1]!.body);expect(writes[0]!.headers).toEqual(writes[1]!.headers);expect(JSON.parse(writes[0]!.body as string).hardwareBindingId).toBe(ids.position)
 })
})


it('maps previous links into the same workspace context without dropping identifiers',async()=>{
 const router=createRouter({history:createMemoryHistory(),routes:applicationRouter.options.routes})
 for(const panel of ['instances','releases','enrollment','deployments']){
  await router.push('/'+panel+'?softwareId='+ids.software+'&id='+ids.instance)
  expect(router.currentRoute.value.path).toBe('/workspace');expect(router.currentRoute.value.query).toEqual({view:'software',panel,softwareId:ids.software,id:ids.instance})
 }
 await router.push('/site?deviceId='+ids.device);expect(router.currentRoute.value.query.deviceId).toBe(ids.device)
 await router.push('/users');expect(router.currentRoute.value.path).toBe('/system')
})
