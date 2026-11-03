import { afterEach, expect, it, vi } from 'vitest'
import { createApp, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import CompatibilityPanel from '../src/components/CompatibilityPanel.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
import type { Release } from '../src/packages'

const sid='fixture-software', rid='fixture-target', source='fixture-source'
const release={id:rid,softwareId:sid,version:'1.0.0',state:'Formal',publishedAt:'2026-10-31T00:00:00Z'} as Release
const account=(write=true)=>({authenticated:true,subjectId:'fixture-person',employeeNo:'FIXTURE',mustChangePassword:false,csrfToken:'fixture',permissions:(write?['software.read','release.upload']:['software.read']).map(operation=>({softwareId:sid,operation}))})
const declaration={id:'declaration-1',targetReleaseId:rid,declarationRevision:1,sourceReleaseIds:[source],databaseMode:'None',databaseRequirements:[],statement:'虚构兼容依据',reason:'虚构登记原因',declaredBy:'fixture-person',createdAt:'2026-10-31T00:00:00Z'}
const clean:(()=>void)[]=[]
afterEach(()=>{clean.splice(0).forEach(f=>f());vi.restoreAllMocks();vi.unstubAllGlobals();document.body.innerHTML=''})
async function settle(){for(let i=0;i<40;i++){await nextTick();await Promise.resolve()}}
function button(text:string){const b=[...document.querySelectorAll('button')].find(x=>x.textContent?.trim()===text);expect(b).toBeTruthy();return b!}
function fill(label:string,value:string){const e=[...document.querySelectorAll('label')].find(x=>x.textContent?.startsWith(label))?.querySelector('input,textarea,select') as HTMLInputElement;expect(e).toBeTruthy();e.value=value;e.dispatchEvent(new Event(e.tagName==='SELECT'?'change':'input',{bubbles:true}))}
async function mount(write=true){const target=ref(release),pinia=createPinia(),session=useSession(pinia);session.current=account(write) as typeof session.current;const pending=ref(false)
 const page={components:{CompatibilityPanel},setup:()=>({target}),template:'<CompatibilityPanel :key="target.id" :release="target" />'}
 const router=createRouter({history:createMemoryHistory(),routes:[{path:'/',component:page},{path:'/other',component:{template:'<p>other</p>'}}]});await router.push('/')
 const host=document.createElement('div');document.body.appendChild(host);const app=createApp({template:'<RouterView />'}).use(pinia).use(router).provide(pendingWriteKey,pending);app.mount(host);clean.push(()=>app.unmount());await settle()
 return {target,session,pending,router}}
function reads(write?:(p:string,o:RequestInit)=>Promise<Response>,read?:(p:string)=>Response|Promise<Response>|undefined){vi.stubGlobal('fetch',vi.fn(async(p:string,o?:RequestInit)=>{if(o?.method==='POST' && write)return write(p,o);const custom=read?.(p);if(custom)return custom;if(p.endsWith('/session'))return Response.json(account());if(p.includes('/releases?'))return Response.json({items:[release,{...release,id:source,version:'2.0.0'}],nextCursor:null});return Response.json({items:[],nextCursor:null})}))}
async function openForm(){button('登记新兼容修订').click();await settle();fill('允许的来源版本',source);fill('兼容说明','虚构兼容依据');fill('登记原因','虚构登记原因');await settle()}
function submit(){document.querySelector('form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}))}

it('compatibility records the complete database set against the fixed target without publishing or scheduling',async()=>{
 const writes:{path:string;body:unknown}[]=[];reads(async(path,o)=>{writes.push({path,body:JSON.parse(o.body as string)});return Response.json(declaration)})
 await mount();await openForm();fill('现场数据库','Present');await settle();fill('数据库标识','recipe');fill('允许的模式标识','v1\nv2');submit();await settle()
 expect(writes).toEqual([{path:'/api/v1/manage/releases/'+rid+'/compatibility-declarations',body:{sourceReleaseIds:[source],databaseMode:'Present',databaseRequirements:[{databaseKey:'recipe',acceptedSchemaIds:['v1','v2']}],statement:'虚构兼容依据',reason:'虚构登记原因'}}]);expect(document.body.textContent).toContain('新兼容修订已登记')
})
it('compatibility keeps original key body and person after a response is lost',async()=>{
 const writes:RequestInit[]=[];reads(async(_p,o)=>{writes.push(o);if(writes.length===1)throw new TypeError('response lost');return Response.json(declaration)})
 const {pending,router}=await mount();await openForm();submit();await settle();expect(writes).toHaveLength(1);expect(pending.value).toBe(true);await router.push('/other');expect(router.currentRoute.value.path).toBe('/')
 expect((document.querySelector('fieldset') as HTMLFieldSetElement).disabled).toBe(true);button('核实原兼容声明').click();await settle();expect(writes).toHaveLength(2);expect(writes[1]!.body).toBe(writes[0]!.body);expect(writes[1]!.headers).toEqual(writes[0]!.headers);expect(pending.value).toBe(false)
})
it('compatibility rejects an old draft after authorization or person changes',async()=>{
 const writes:RequestInit[]=[];reads(async(_p,o)=>{writes.push(o);return Response.json(declaration)})
 const {session}=await mount();await openForm();session.current={...account(false),subjectId:'another-person'} as typeof session.current;await settle();submit();await settle();expect(writes).toHaveLength(0);expect(document.body.textContent).toContain('操作对象或授权已变化');expect((document.querySelector('fieldset') as HTMLFieldSetElement).disabled).toBe(true)
})
it('compatibility drops delayed history when the release panel changes',async()=>{
 let resolve!:(r:Response)=>void;const waiting=new Promise<Response>(r=>{resolve=r});reads(undefined,p=>p.includes('/'+rid+'/compatibility-declarations')?waiting:undefined)
 const {target}=await mount();const details=document.querySelector('details')!;details.open=true;details.dispatchEvent(new Event('toggle'));await settle();target.value={...release,id:'another-target',version:'0.9.0'};await settle();resolve(Response.json({items:[declaration],nextCursor:null}));await settle();expect(document.body.textContent).not.toContain('虚构兼容依据');expect(document.body.textContent).toContain('0.9.0')
})
it('compatibility read-only users see history without a registration action',async()=>{
 reads(undefined,p=>p.includes('compatibility-declarations')?Response.json({items:[declaration],nextCursor:null}):undefined);await mount(false);const details=document.querySelector('details')!;details.open=true;details.dispatchEvent(new Event('toggle'));await settle();expect(document.body.textContent).toContain('虚构兼容依据');expect([...document.querySelectorAll('button')].some(x=>x.textContent==='登记新兼容修订')).toBe(false)
})
