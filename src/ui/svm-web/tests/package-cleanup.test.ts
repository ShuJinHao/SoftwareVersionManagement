import { afterEach, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import PackageCleanupPanel from '../src/components/PackageCleanupPanel.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
import type { Release } from '../src/packages'
const sid='fixture-software',pid='fixture-package'
const release={id:'fixture-release',softwareId:sid,packageId:pid,version:'1.0.0',state:'Disabled'} as Release
const account=(clean=true)=>({authenticated:true,subjectId:'fixture-person',employeeNo:'FIXTURE',mustChangePassword:false,csrfToken:'fixture',permissions:(clean?['software.read','package.clean']:['software.read']).map(operation=>({softwareId:sid,operation}))})
const status={packageId:pid,revision:6,state:'Ready',workId:null,blockingReasons:[],openDownloads:[],moreDownloadGenerations:false}
const group={nodeId:'node-a',workerGeneration:'fixture-generation',openCount:2,latestStartedAt:'2026-11-03T00:00:00Z'}
const clean:(()=>void)[]=[]
afterEach(()=>{clean.splice(0).forEach(f=>f());vi.restoreAllMocks();vi.unstubAllGlobals();document.body.innerHTML=''})
async function settle(){for(let i=0;i<40;i++){await nextTick();await Promise.resolve()}}
function button(text:string){const b=[...document.querySelectorAll('button')].find(x=>x.textContent?.trim()===text);expect(b).toBeTruthy();return b!}
function fill(label:string,value:string){const e=[...document.querySelectorAll('label')].find(x=>x.textContent?.startsWith(label))?.querySelector('input,textarea') as HTMLInputElement;expect(e).toBeTruthy();e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}))}
function confirm(){const e=document.querySelector('input[type=checkbox]') as HTMLInputElement;e.checked=true;e.dispatchEvent(new Event('change',{bubbles:true}))}
function submit(){document.querySelector('form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}))}
async function mount(allowed=true){
 const target=ref(release),pinia=createPinia(),session=useSession(pinia);session.current=account(allowed) as typeof session.current;const pending=ref(false)
 const changed=vi.fn();const page={components:{PackageCleanupPanel},setup:()=>({target,changed}),template:'<PackageCleanupPanel :release="target" @changed="changed" />'}
 const router=createRouter({history:createMemoryHistory(),routes:[{path:'/',component:page},{path:'/other',component:{template:'<p>other</p>'}}]});await router.push('/')
 const host=document.createElement('div');document.body.appendChild(host);const app=createApp({template:'<RouterView />'}).use(pinia).use(router).provide(pendingWriteKey,pending);app.mount(host);clean.push(()=>app.unmount());await settle()
 return {target,session,pending,router,changed}
}
function fetches(write?:(p:string,o:RequestInit)=>Promise<Response>,read?:(p:string)=>Response|Promise<Response>|undefined){
 vi.stubGlobal('fetch',vi.fn(async(p:string,o?:RequestInit)=>{if(o?.method==='POST'&&write)return write(p,o);const custom=read?.(p);if(custom)return custom;if(p.endsWith('/session'))return Response.json(account());return Response.json(status)}))
}
async function open(){const e=document.querySelector('details')!;e.open=true;e.dispatchEvent(new Event('toggle'));await settle()}
async function cleanupForm(){await open();button('清理包文件').click();await settle();fill('操作原因','虚构清理原因');confirm();await settle()}

it('cleanup fixes package revision and requires a deliberate deletion confirmation',async()=>{
 const writes:{path:string;body:unknown}[]=[];fetches(async(path,o)=>{writes.push({path,body:JSON.parse(o.body as string)});return Response.json({id:'work-1',state:'Pending'})})
 await mount();await open();button('清理包文件').click();await settle();fill('操作原因','虚构清理原因');submit();await settle();expect(writes).toHaveLength(0)
 confirm();submit();await settle();expect(writes).toEqual([{path:'/api/v1/manage/packages/'+pid+'/cleanup',body:{reason:'虚构清理原因',expectedRevision:6}}]);expect(document.body.textContent).toContain('版本和使用历史继续保留')
})
it('cleanup refuses blocked work and never turns a missing download log into a timeout-based success',async()=>{
 fetches(undefined,()=>Response.json({...status,blockingReasons:['TASKS_UNFINISHED','DOWNLOADS_OPEN'],openDownloads:[group]}));await mount();await open()
 expect(button('清理包文件').disabled).toBe(true);expect(document.body.textContent).toContain('仍有未结束的投放或任务');expect(document.body.textContent).toContain('超时不能代替确认')
 button('登记已停止证据').click();await settle();expect(document.body.textContent).toContain('fixture-generation');expect(button('保存停止证据').disabled).toBe(true)
})
it('cleanup keeps original key body and subject after a lost response and blocks navigation',async()=>{
 const writes:RequestInit[]=[];fetches(async(_p,o)=>{writes.push(o);if(writes.length===1)throw new TypeError('lost');return Response.json({id:'work-1'})})
 const {router,pending}=await mount();await cleanupForm();submit();await settle();expect(writes).toHaveLength(1);expect(pending.value).toBe(true)
 await router.push('/other');expect(router.currentRoute.value.path).toBe('/');button('核实原操作').click();await settle();expect(writes).toHaveLength(2);expect(writes[1]!.body).toBe(writes[0]!.body);expect(writes[1]!.headers).toEqual(writes[0]!.headers)
})
it('cleanup prevents a draft from switching to another package or another person',async()=>{
 const writes:RequestInit[]=[];fetches(async(_p,o)=>{writes.push(o);return Response.json({id:'work-1'})})
 const {target,session}=await mount();await cleanupForm();target.value={...release,packageId:'other-package',version:'2.0.0'};session.current={...account(),subjectId:'other-person'} as typeof session.current;await settle()
 submit();await settle();expect(writes).toHaveLength(0);expect(document.querySelector('form')?.textContent).toContain('1.0.0');expect(document.body.textContent).toContain('不能提交原草稿')
})
it('cleanup discards late success and late failure after changing the target',async()=>{
 let resolve!:(r:Response)=>void;const waiting=new Promise<Response>(r=>{resolve=r});fetches(undefined,p=>p.includes(pid)?waiting:Response.json({...status,packageId:'other-package'}))
 const {target}=await mount();await open();target.value={...release,packageId:'other-package'};await settle();button('重新核对清理条件').click();await settle()
 resolve(Response.json({code:'DEPENDENCY_UNAVAILABLE'},{status:503}));await settle();expect(document.body.textContent).not.toContain('服务暂不可用');expect(button('清理包文件').disabled).toBe(false)
})
it('cleanup stop evidence retains exact node generation and does not claim bytes or download completion',async()=>{
 const writes:{path:string;body:Record<string,unknown>}[]=[];fetches(async(path,o)=>{writes.push({path,body:JSON.parse(o.body as string)});return Response.json({id:'evidence-1'})},()=>Response.json({...status,blockingReasons:['DOWNLOADS_OPEN'],openDownloads:[group]}))
 await mount();await open();button('登记已停止证据').click();await settle();fill('实际停止时间','2026-11-03T09:00:00');fill('停止证据','虚构节点进程退出证据');fill('操作原因','确认历史日志无法补传');confirm();submit();await settle()
 expect(writes).toHaveLength(1);expect(writes[0]!.path).toContain('/download-stop-evidence');expect(writes[0]!.body).toMatchObject({nodeId:'node-a',workerGeneration:'fixture-generation',processStoppedConfirmed:true,expectedRevision:6})
 expect(writes[0]!.body).not.toHaveProperty('bytesSent');expect(writes[0]!.body).not.toHaveProperty('outcome');expect(document.body.textContent).toContain('不表示下载成功')
})
it('cleanup is unavailable without product cleanup permission',async()=>{fetches();await mount(false);expect(document.body.textContent).not.toContain('清理停用版本的文件')})

it('cleanup refreshes surrounding version facts on completion without repeating the refresh',async()=>{
 let finished=false
 fetches(undefined,p=>p.includes('/package-work/')?Response.json({id:'work-1',state:finished?'Completed':'Running',completedItems:finished?2:0,totalItems:2}):Response.json({...status,state:finished?'Cleaned':'Cleaning',workId:'work-1'}))
 const {changed,target}=await mount();await open();expect(changed).toHaveBeenCalledTimes(1)
 finished=true;button('重新核对清理条件').click();await settle()
 expect(document.body.textContent).toContain('两个节点的包文件均已清理');expect(changed).toHaveBeenCalledTimes(2)
 target.value={...release};await settle();expect(document.body.textContent).toContain('两个节点的包文件均已清理');expect(document.body.textContent).not.toContain('对象或权限已变化')
 button('重新核对清理条件').click();await settle();expect(changed).toHaveBeenCalledTimes(2)
})
