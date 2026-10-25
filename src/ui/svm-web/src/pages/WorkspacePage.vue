<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { Monitor, Cpu, Layers3, Plus, Search, ChevronRight, ArrowLeft, Package, RefreshCw } from '@lucide/vue'
import { ApiError, request } from '../api'
import { type Device, type Page, type Process, type Site, type Software, categoryName } from '../catalog'
import { type Hardware, type Installation, type Position, type WorkspaceDevice, type WorkspacePage, mergeRows, queryString } from '../hardware'
import { type Grant, type Instance, generateSecret } from '../instances'
import { useOwnedReads } from '../ownedReads'
import { useProtectedMutation } from '../protectedMutation'
import { pendingWriteKey } from '../mutation'
import { useSession } from '../session'
import OperationStatus from '../components/OperationStatus.vue'
import InstallationTable from '../components/InstallationTable.vue'
import InstancesPage from './InstancesPage.vue'
import ReleasesPage from './ReleasesPage.vue'
import DeploymentsPage from './DeploymentsPage.vue'
import EnrollmentPage from './EnrollmentPage.vue'

const session = useSession(), route = useRoute(), router = useRouter(), mutation = useProtectedMutation()
const pending = inject(pendingWriteKey, ref(false)), failure = ref(''), notice = ref('')
const site = ref<Site | null>(null), processes = ref<Process[]>([]), processNext = ref<string | null>(null)
const processSearch = ref(''), appliedProcessSearch = ref(''), softwareSearch = ref(''), appliedSoftwareSearch = ref('')
const software = ref<Software[]>([]), softwareNext = ref<string | null>(null), selectedSoftware = ref<Software | null>(null)
const devices = ref<WorkspaceDevice[]>([]), deviceNext = ref<string | null>(null), deviceTotal = ref(0), deviceSearch = ref(''), appliedDeviceSearch = ref('')
const device = ref<Device | null>(null), hardware = ref<Hardware[]>([]), hardwareNext = ref<string | null>(null)
const rows = ref<Installation[]>([]), rowNext = ref<string | null>(null), rowTotal = ref(0), readAt = ref('')
const selected = ref(new Map<string, Installation>()), deploymentTargets = ref<Instance[]>([])
const view = computed(() => route.query.view === 'software' || !session.canAssets ? 'software' : 'site')
const processId = computed(() => String(route.query.processId ?? '')), deviceId = computed(() => String(route.query.deviceId ?? ''))
const softwareId = computed(() => view.value === 'software' ? String(route.query.softwareId ?? '') : '')
const panel = computed(() => String(route.query.panel ?? ''))
const activeProcess = computed(() => processes.value.find(p => p.id === processId.value))
const selectedIds = computed(() => new Set(selected.value.keys()))
const panelSoftware = computed(() => String(route.query.softwareId ?? ''))
const canPanel = computed(() => panel.value === 'releases' ? session.can('software.read', panelSoftware.value) : panel.value === 'enrollment' ? session.can('enrollment.manage', panelSoftware.value) : panel.value === 'deployments' ? session.can('instance.read', panelSoftware.value) || session.can('deployment.create', panelSoftware.value) : panel.value === 'instances' && session.can('instance.read', panelSoftware.value))
const groups = computed(() => mergeRows(hardware.value, rows.value.flatMap(r => r.hardware ? [r.hardware] : []), x => x.id).map(h => ({ hardware: h, rows: rows.value.filter(r => r.hardware?.id === h.id) })))
const legacyRows = computed(() => rows.value.filter(r => !r.hardware))
const locked = computed(() => pending.value || mutation.blocked.value)
const dialog = ref<HTMLDialogElement | null>(null)
const returnLocation=ref<Record<string,string>|null>(null)
type Action = 'editSoftware' | 'process' | 'editProcess' | 'device' | 'editDevice' | 'hardware' | 'editHardware' | 'associate' | 'revoke' | 'assign' | 'grant'
interface Draft { action: Action; epoch: number; process: Process | null; device: Device | null; hardware: Hardware | null; row: Installation | null; software: Software | null }
const draft = ref<Draft | null>(null), name = ref(''), assetNumber = ref(''), managementIp = ref(''), hardwareType = ref('IPC')
const processChoice = ref(''), reason = ref(''), choice = ref('existing'), category = ref('UpperComputer'), description = ref(''), candidateId = ref('')
const candidates = ref<Software[]>([]), candidateSearch = ref(''), candidateApplied = ref(''), candidateNext = ref<string | null>(null)
const vacant = ref<Installation[]>([]), vacantNext = ref<string | null>(null), positionChoice = ref('')
const expires = ref(''), secret = ref(''), issued = ref<Grant | null>(null)
let previousFocus: HTMLElement | null = null
const reads = useOwnedReads(() => {
  site.value = null; processes.value = []; software.value = []; devices.value = []; device.value = null; hardware.value = []; rows.value = []
  processNext.value = softwareNext.value = deviceNext.value = hardwareNext.value = rowNext.value = null
  selected.value.clear(); deploymentTargets.value = []; selectedSoftware.value = null; candidates.value = []; vacant.value = []; secret.value = ''; issued.value = null
})
const validDraft = computed(() => !!draft.value && draft.value.epoch === reads.epoch.value)
const get = <T,>(path: string, signal: AbortSignal) => request<T>(path, 'GET', undefined, undefined, undefined, { signal })
async function fail(e: unknown) { if (e instanceof ApiError && e.cancelled) return; failure.value = e instanceof Error ? e.message : '读取失败，请重试。'; if (e instanceof ApiError && [401,403].includes(e.status)) await session.load() }
async function loadSite() {
  if (session.canAssets) await reads.run('site',true,s=>get<Site>('/api/v1/manage/site',s),v=>{site.value=v},fail)
}
async function loadProcesses(cursor?: string | null) {
  if (!session.canAssets) return
  const applied = cursor ? appliedProcessSearch.value : processSearch.value.trim()
  await reads.run('processes', !cursor, s => get<Page<Process>>('/api/v1/manage/processes?' + queryString({ name: applied }, cursor), s), p => {
    processes.value = cursor ? mergeRows(processes.value,p.items,x=>x.id) : p.items; processNext.value = p.nextCursor; appliedProcessSearch.value = applied
  }, fail)
}
async function loadSoftware(cursor?: string | null) {
  if (!session.canSoftware) return
  const applied = cursor ? appliedSoftwareSearch.value : softwareSearch.value.trim()
  await reads.run('software', !cursor, s => get<Page<Software>>('/api/v1/manage/software?' + queryString({ name: applied }, cursor), s), p => {
    software.value = cursor ? mergeRows(software.value,p.items,x=>x.id) : p.items; softwareNext.value = p.nextCursor; appliedSoftwareSearch.value = applied
  }, fail)
}
async function loadDevices(cursor?: string | null) {
  if (!session.canAssets || view.value !== 'site' || deviceId.value) return
  const applied = cursor ? appliedDeviceSearch.value : deviceSearch.value.trim(), process = processId.value
  await reads.run('devices', !cursor, s => get<WorkspacePage<WorkspaceDevice>>('/api/v1/manage/workspace/devices?' + queryString({ processId: process, name: applied }, cursor), s), p => {
    devices.value = cursor ? mergeRows(devices.value,p.items,x=>x.device.id) : p.items; deviceNext.value = p.nextCursor; deviceTotal.value = p.totalCount; appliedDeviceSearch.value = applied
  }, fail)
}
function installationFilter() { return view.value === 'software' ? { softwareId: softwareId.value } : { deviceId: deviceId.value } }
async function loadRows(cursor?: string | null) {
  if (!session.canAssets || !deviceId.value && !softwareId.value) return
  const filter = installationFilter()
  await reads.run('rows', !cursor, s => get<WorkspacePage<Installation>>('/api/v1/manage/workspace/installations?' + queryString(filter,cursor), s), p => {
    rows.value = cursor ? mergeRows(rows.value,p.items,x=>x.rowId) : p.items; rowNext.value = p.nextCursor; rowTotal.value = p.totalCount; readAt.value = p.serverTime
  }, fail)
}
async function moreHardware() {
  if (!hardwareNext.value || !device.value) return
  const id = device.value.id, cursor = hardwareNext.value
  await reads.run('hardware', false, s => get<Page<Hardware>>(`/api/v1/manage/devices/${id}/hardware?` + queryString({},cursor),s), p => { hardware.value = mergeRows(hardware.value,p.items,x=>x.id); hardwareNext.value = p.nextCursor }, fail)
}
async function loadContext() {
  reads.invalidate('devices','context','hardware','rows'); failure.value = ''; selected.value.clear()
  device.value = null; hardware.value = []; rows.value = []; devices.value = []; selectedSoftware.value = null
  rowNext.value = hardwareNext.value = deviceNext.value = null; readAt.value = ''; deviceTotal.value = rowTotal.value = 0
  if (view.value === 'site' && deviceId.value && session.canAssets) {
    const id = deviceId.value
    await reads.run('context',true, async s => {
      const [d,h,p] = await Promise.all([get<Device>(`/api/v1/manage/devices/${id}`,s), get<Page<Hardware>>(`/api/v1/manage/devices/${id}/hardware?pageSize=50`,s),get<WorkspacePage<Installation>>('/api/v1/manage/workspace/installations?'+queryString({deviceId:id}),s)])
      return {d,h,p}
    }, ({d,h,p}) => { device.value=d; hardware.value=h.items; hardwareNext.value=h.nextCursor; rows.value=p.items; rowNext.value=p.nextCursor; rowTotal.value=p.totalCount; readAt.value=p.serverTime }, fail)
  } else if (view.value === 'software' && softwareId.value) {
    const id = softwareId.value
    await reads.run('context',true,async s => {
      const [product,p] = await Promise.all([get<Software>(`/api/v1/manage/software/${id}`,s),session.canAssets && session.can('instance.read',id) ? get<WorkspacePage<Installation>>('/api/v1/manage/workspace/installations?'+queryString({softwareId:id}),s) : Promise.resolve(null)])
      return {product,p}
    }, ({product,p}) => { selectedSoftware.value=product; rows.value=p?.items ?? []; rowNext.value=p?.nextCursor ?? null; rowTotal.value=p?.totalCount ?? 0; readAt.value=p?.serverTime ?? '' },fail)
  } else await loadDevices()
}
async function navigate(query: Record<string,string>) { if (locked.value || draft.value) return; await router.push({path:'/workspace',query}) }
function selectProcess(id: string) { void navigate({processId:id}) }
function selectDevice(d: Device) { void navigate({processId:d.processId,deviceId:d.id}) }
function selectSoftware(s: Software) { if(view.value==='site') returnLocation.value={processId:processId.value,deviceId:deviceId.value}; void navigate({view:'software',softwareId:s.id}) }
async function openPanel(row: Installation | null, target: string) {
  if (locked.value || draft.value) return
  const id = row?.software.id ?? selectedSoftware.value?.id; if (!id) return
  await router.push({path:'/workspace',query:{...route.query,panel:target,softwareId:id,channel:'Formal',id:target==='instances' ? row?.instance?.id : undefined}})
}
function closePanel() { if (locked.value) return; deploymentTargets.value=[]; const {panel:_p,id:_id,...query}=route.query; void router.push({path:'/workspace',query}) }
function toggle(row: Installation) {
  if (locked.value || !row.instance || !row.position || !session.can('deployment.create',row.software.id)) return
  const next=new Map(selected.value); if(next.has(row.instance.id)) next.delete(row.instance.id)
  else if(next.size && [...next.values()][0]!.software.id!==row.software.id) {notice.value='一次更新选择同一软件的安装位置。请先取消之前的选择。'; return}
  else next.set(row.instance.id,JSON.parse(JSON.stringify(row)) as Installation)
  selected.value=next
}
async function updateSelected() {
  const values=[...selected.value.values()]; if(!values.length || locked.value) return
  deploymentTargets.value=values.map(x=>({...x.instance!})); await openPanel(values[0]!, 'deployments')
}
async function open(action: Action, h: Hardware | null=null, row: Installation | null=null) {
  if(locked.value || draft.value) return
  previousFocus=document.activeElement instanceof HTMLElement ? document.activeElement : null
  draft.value=JSON.parse(JSON.stringify({action,epoch:reads.epoch.value,process:activeProcess.value ?? null,device:row?.device ?? device.value,hardware:h ?? row?.hardware ?? null,row,software:row?.software ?? selectedSoftware.value})) as Draft
  name.value=action==='editSoftware' ? selectedSoftware.value!.name : action==='editHardware' ? h!.name : action==='editDevice' ? device.value!.name : action==='editProcess' ? activeProcess.value!.name : ''
  assetNumber.value=action==='editHardware' ? h!.assetNumber ?? '' : action==='editDevice' ? device.value!.assetNumber ?? '' : ''
  managementIp.value=action==='editHardware' ? h!.managementIp ?? '' : ''; hardwareType.value='IPC'; processChoice.value=device.value?.processId ?? processId.value
  reason.value=''; choice.value='existing'; category.value='UpperComputer'; description.value=action==='editSoftware' ? selectedSoftware.value?.description ?? '' : ''; candidateId.value=''; candidateSearch.value=''; candidates.value=[]; candidateNext.value=null
  vacant.value=[]; vacantNext.value=null; positionChoice.value=''; expires.value=''; secret.value=action==='grant' ? generateSecret() : ''; issued.value=null; mutation.error.value=''
  await nextTick(); dialog.value?.showModal()
  if(action==='associate') await loadCandidates()
  if(action==='assign') await loadVacant()
}
function closeDialog() { if(locked.value) return; dialog.value?.close(); draft.value=null; secret.value=''; issued.value=null; reads.invalidate('candidates','vacant'); previousFocus?.focus() }
async function loadCandidates(cursor?: string | null) {
  const applied=cursor ? candidateApplied.value : candidateSearch.value.trim()
  await reads.run('candidates',!cursor,s=>get<Page<Software>>('/api/v1/manage/software?'+queryString({name:applied},cursor),s),p=>{candidates.value=cursor ? mergeRows(candidates.value,p.items,x=>x.id) : p.items; candidateNext.value=p.nextCursor; candidateApplied.value=applied},fail)
}
async function loadVacant(cursor?: string | null) {
  const row=draft.value?.row; if(!row) return
  await reads.run('vacant',!cursor,s=>get<WorkspacePage<Installation>>('/api/v1/manage/workspace/installations?'+queryString({deviceId:row.device.id,softwareId:row.software.id},cursor),s),p=>{const free=p.items.filter(x=>x.position && !x.instance); vacant.value=cursor ? mergeRows(vacant.value,free,x=>x.rowId) : free; vacantNext.value=p.nextCursor},fail)
}
async function save() {
  const d=draft.value; if(!d || mutation.busy.value || !validDraft.value && !mutation.uncertain.value) return
  const a=d.action; let path='', method='POST', body:unknown
  if(mutation.pending.value) {path=mutation.pending.value.path; method=mutation.pending.value.method; body=mutation.pending.value.body}
  else if(a==='editSoftware') {path='/api/v1/manage/software/'+d.software!.id;method='PATCH';body={name:name.value,description:description.value || null,expectedRevision:d.software!.revision,reason:reason.value}}
  else if(a==='process') {path='/api/v1/manage/processes';body={name:name.value}}
  else if(a==='editProcess') {path='/api/v1/manage/processes/'+d.process!.id;method='PATCH';body={name:name.value,expectedRevision:d.process!.revision,reason:reason.value}}
  else if(a==='device') {path='/api/v1/manage/devices';body={name:name.value,processId:processChoice.value,assetNumber:assetNumber.value || null}}
  else if(a==='editDevice') {path='/api/v1/manage/devices/'+d.device!.id;method='PATCH';body={name:name.value,processId:processChoice.value,assetNumber:assetNumber.value || null,expectedRevision:d.device!.revision,reason:reason.value}}
  else if(a==='hardware' || a==='editHardware') {path=a==='hardware' ? `/api/v1/manage/devices/${d.device!.id}/hardware` : '/api/v1/manage/hardware/'+d.hardware!.id;method=a==='hardware'?'POST':'PATCH';body={name:name.value,assetNumber:assetNumber.value || null,managementIp:managementIp.value || null,...(a==='hardware' ? {type:hardwareType.value} : {expectedRevision:d.hardware!.revision,reason:reason.value})}}
  else if(a==='associate') {path=`/api/v1/manage/hardware/${d.hardware!.id}/software-bindings`;body={...(choice.value==='existing' ? {softwareId:candidateId.value} : {newSoftware:{name:name.value,category:category.value,description:description.value || null}}),reason:reason.value}}
  else if(a==='revoke') {path=`/api/v1/manage/hardware/${d.row!.hardware!.id}/software-bindings/${d.row!.software.id}`;method='DELETE';body={expectedRevision:d.row!.position!.revision,reason:reason.value}}
  else if(a==='assign') {const p=vacant.value.find(x=>x.position?.id===positionChoice.value)?.position;if(!p) return;path=`/api/v1/manage/instances/${d.row!.instance!.id}/hardware-binding`;method='PUT';body={hardwareBindingId:p.id,expectedBindingRevision:p.revision,expectedRevision:d.row!.instance!.revision,reason:reason.value}}
  else if(a==='grant') {if(!Number.isFinite(Date.parse(expires.value))) return;path='/api/v1/manage/enrollment-grants';body={softwareId:d.row!.software.id,deviceIds:[d.row!.device.id],hardwareBindingId:d.row!.position!.id,hardwareBindingRevision:d.row!.position!.revision,maxInstances:1,expiresAt:new Date(expires.value).toISOString(),secretMaterial:secret.value,reason:reason.value}}
  const result=await mutation.perform<unknown>(path,method,body,session.current?.csrfToken ?? '')
  if(mutation.completed.value) {
    notice.value='操作已确认。'; if(a==='grant') {issued.value=result as Grant;return}
    closeDialog(); await session.load(); await Promise.all([loadSite(),loadProcesses(),loadSoftware(),loadContext()])
  }
}
const titles: Record<Action,string>={editSoftware:'修改软件资料',process:'创建工序',editProcess:'修改工序',device:'创建设备',editDevice:'修改设备',hardware:'添加硬件',editHardware:'修改硬件',associate:'关联软件',revoke:'解除软件关联',assign:'补充实例归属',grant:'接入此安装位置'}
onBeforeRouteUpdate(()=>{if(locked.value || draft.value){notice.value='请先完成或关闭当前操作；结果未知时须先核实。';return false}})
watch(()=>[view.value,processId.value,deviceId.value,softwareId.value],()=>{void loadContext()})
onMounted(async()=>{await loadSite(); await Promise.all([loadProcesses(),loadSoftware(),loadContext()])})
onUnmounted(()=>{secret.value='';deploymentTargets.value=[]})
</script>

<template>
<section class="software-workspace">
  <div class="work-heading"><div><div class="eyebrow">{{ site?.siteName ?? '制造业软件版本管控平台' }}</div><h1>软件工作台</h1><p class="muted">找到设备与软件，查看安装事实，管理现场更新。</p></div><span class="scope-chip"><Layers3 :size="15" />当前授权范围</span></div>
  <p v-if="failure" role="alert" class="error">{{ failure }} <button @click="loadContext">重试读取</button></p><p v-if="notice" role="status" class="workspace-notice">{{ notice }}</p>
  <template v-if="panel"><div class="context-header"><button :disabled="locked" @click="closePanel"><ArrowLeft :size="15" />返回工作台</button><span>{{ device?.name ?? selectedSoftware?.name ?? '当前软件' }}</span></div>
    <div v-if="canPanel || locked" class="context-body"><ReleasesPage v-if="panel==='releases'" :key="'release-'+panelSoftware" /><InstancesPage v-else-if="panel==='instances'" :key="'instance-'+String(route.query.id)" :contextual="!!route.query.id" /><DeploymentsPage v-else-if="panel==='deployments'" :key="'task-'+panelSoftware" :preset-targets="deploymentTargets.length ? deploymentTargets : undefined" /><EnrollmentPage v-else-if="panel==='enrollment'" :key="'grant-'+panelSoftware" /></div>
    <p v-else class="empty">当前权限不足以查看此面板，请联系人员管理员。</p>
  </template>
  <div v-else class="work-layout">
    <aside class="work-selector"><div class="view-switch"><button :class="{active:view==='site'}" :disabled="locked || !session.canAssets" @click="navigate({})">按工序</button><button :class="{active:view==='software'}" :disabled="locked || !session.canSoftware" @click="navigate({view:'software'})">找软件</button></div>
      <template v-if="view==='site'"><form class="selector-search" @submit.prevent="loadProcesses()"><input v-model="processSearch" aria-label="搜索工序" placeholder="搜索工序" /><button aria-label="查询工序" :disabled="reads.pending('processes')"><Search :size="16" /></button></form>
        <button class="selector-item" :class="{active:!processId}" :disabled="locked" @click="selectProcess('')"><Layers3 :size="16" />全部工序</button>
        <button v-for="p in processes" :key="p.id" class="selector-item" :class="{active:processId===p.id}" :disabled="locked" @click="selectProcess(p.id)"><Layers3 :size="16" /><span>{{ p.name }}<small>{{ p.code }}</small></span><ChevronRight :size="14" /></button>
        <button v-if="processNext" class="selector-more" :disabled="reads.pending('processes')" @click="loadProcesses(processNext)">更多工序</button>
        <button v-if="session.can('asset.manage')" class="selector-more" :disabled="locked" @click="open('process')"><Plus :size="14" />创建工序</button>
      </template>
      <template v-else><form class="selector-search" @submit.prevent="loadSoftware()"><input v-model="softwareSearch" aria-label="搜索软件" placeholder="搜索软件名称" /><button aria-label="查询软件" :disabled="reads.pending('software')"><Search :size="16" /></button></form>
        <button v-for="s in software" :key="s.id" class="selector-item" :class="{active:softwareId===s.id}" :disabled="locked" @click="selectSoftware(s)"><Package :size="16" /><span>{{ s.name }}<small>{{ categoryName(s.category) }}</small></span><ChevronRight :size="14" /></button>
        <p v-if="!software.length" class="empty">暂无可查看的软件。</p><button v-if="softwareNext" class="selector-more" :disabled="reads.pending('software')" @click="loadSoftware(softwareNext)">更多软件</button>
        <p class="selector-hint">在设备的工控机下新建或关联软件。同一软件可用于多台设备。</p>
      </template>
    </aside>
    <div class="work-content">
      <template v-if="view==='site' && !deviceId"><div class="content-heading"><div><h2>{{ activeProcess?.name ?? '全部设备' }}</h2><span class="muted">{{ deviceTotal }} 台设备 · 选择设备查看具体硬件</span></div><div class="row-actions"><button v-if="activeProcess && session.can('asset.manage')" :disabled="locked" @click="open('editProcess')">修改工序</button><button v-if="session.can('asset.manage')" class="primary" :disabled="locked || !processes.length" @click="open('device')"><Plus :size="15" />创建设备</button></div></div>
        <form class="work-filter" @submit.prevent="loadDevices()"><input v-model="deviceSearch" placeholder="搜索设备名称或编号" aria-label="搜索设备" /><button :disabled="reads.pending('devices')"><Search :size="16" />查询</button></form>
        <div class="table-wrap" tabindex="0" aria-label="工序设备" role="region"><table><thead><tr><th>设备</th><th>工序</th><th>硬件</th><th>软件位置</th><th>待补充归属</th><th>上报待关注</th><th></th></tr></thead><tbody><tr v-for="d in devices" :key="d.device.id"><td><button class="text-button" @click="selectDevice(d.device)">{{ d.device.name }}</button><small>{{ d.device.deviceNo }}<template v-if="d.device.assetNumber"> · {{ d.device.assetNumber }}</template></small></td><td>{{ d.device.processName }}</td><td>{{ d.hardwareCount }}</td><td>{{ d.installationCount }}</td><td>{{ d.pendingLocationCount || '—' }}</td><td>{{ d.unknownCount }} 未知 / {{ d.neverReportedCount }} 未上报</td><td><button @click="selectDevice(d.device)">查看<ChevronRight :size="14" /></button></td></tr></tbody></table><p v-if="!devices.length" class="empty">{{ reads.pending('devices') ? '正在读取设备…' : '暂无设备，可在此工序下创建设备。' }}</p></div>
        <footer class="pagination"><span>已显示 {{ devices.length }} / {{ deviceTotal }} 台</span><button v-if="deviceNext" :disabled="reads.pending('devices')" @click="loadDevices(deviceNext)">加载更多设备</button></footer>
      </template>
      <template v-else-if="device || selectedSoftware"><div class="content-heading"><div><button v-if="device" class="text-button" @click="selectProcess(device.processId)">{{ device.processName }} /</button><h2>{{ device?.name ?? selectedSoftware?.name }}</h2><small>{{ device ? `${device.deviceNo}${device.assetNumber ? ' · 资产号 '+device.assetNumber : ''}` : categoryName(selectedSoftware!.category)+' · 关联设备及安装位置' }}</small></div><div class="row-actions"><template v-if="device && session.can('asset.manage')"><button :disabled="locked" @click="open('editDevice')">设备资料</button><button class="primary" :disabled="locked" @click="open('hardware')"><Plus :size="15" />添加硬件</button></template><template v-if="selectedSoftware"><button v-if="returnLocation" :disabled="locked" @click="navigate(returnLocation)">返回原现场</button><button v-if="session.can('release.upload',selectedSoftware.id)" :disabled="locked" @click="open('editSoftware')">软件资料</button><button :disabled="locked" @click="openPanel(null,'releases')">版本与来源</button><button v-if="session.can('instance.read',selectedSoftware.id)" :disabled="locked" @click="openPanel(null,'deployments')">更新记录</button><button v-if="session.can('enrollment.manage',selectedSoftware.id)" :disabled="locked" @click="openPanel(null,'enrollment')">接入许可记录</button></template><button :disabled="locked || reads.busy.value" aria-label="刷新工作台" @click="loadContext"><RefreshCw :size="16" /></button></div></div>
        <div v-if="selected.size" class="selection-bar"><span>已选 {{ selected.size }} 个安装位置 · {{ [...selected.values()][0]?.software.name }}</span><button class="primary" :disabled="locked" @click="updateSelected">更新所选软件</button><button :disabled="locked" @click="selected.clear()">取消选择</button></div>
        <template v-if="device"><section v-for="g in groups" :key="g.hardware.id" class="hardware-section"><header><span class="hardware-icon"><Monitor v-if="g.hardware.type==='IPC'" :size="20" /><Cpu v-else :size="20" /></span><div><h3>{{ g.hardware.name }}</h3><small>{{ g.hardware.type==='IPC' ? '工控机' : 'PLC 控制器' }} · 管理 IP {{ g.hardware.managementIp ?? '未填写' }}<template v-if="g.hardware.assetNumber"> · {{ g.hardware.assetNumber }}</template></small></div><div class="row-actions"><button v-if="session.can('asset.manage')" :disabled="locked" @click="open('editHardware',g.hardware)">资料</button><button v-if="g.hardware.type==='IPC' && session.can('asset.manage')" :disabled="locked" @click="open('associate',g.hardware)"><Plus :size="14" />关联软件</button></div></header>
          <InstallationTable v-if="g.hardware.type==='IPC'" :rows="g.rows" :selected="selectedIds" :disabled="locked" :time-zone="site?.siteTimeZone ?? 'UTC'" @toggle="toggle" @software="selectSoftware($event.software)" @open="openPanel" @grant="open('grant',null,$event)" @bind="open('assign',null,$event)" @revoke="open('revoke',null,$event)" />
          <p v-else class="hardware-empty">控制器台账已建立。PLC 程序归档、下载和人工使用记录将在后续批次开放。</p>
        </section><p v-if="!groups.length" class="empty">此设备尚无硬件。添加工控机或 PLC 控制器后，再关联程序。</p><button v-if="hardwareNext" :disabled="reads.pending('hardware')" @click="moreHardware">加载更多硬件</button>
          <section v-if="legacyRows.length" class="hardware-section legacy-section"><header><h3>待补充硬件归属</h3><small>历史身份和上报继续保留，补充归属后方可新建更新任务。</small></header><InstallationTable :rows="legacyRows" :selected="selectedIds" :disabled="locked" :time-zone="site?.siteTimeZone ?? 'UTC'" @toggle="toggle" @software="selectSoftware($event.software)" @open="openPanel" @grant="open('grant',null,$event)" @bind="open('assign',null,$event)" @revoke="open('revoke',null,$event)" /></section>
        </template>
        <template v-else><p v-if="!session.canAssets || !session.can('instance.read',selectedSoftware!.id)" class="empty">查看关联设备和安装状态需要台账查看及该软件的实例查看权限。版本与来源仍可单独查看。</p><InstallationTable v-else :rows="rows" :selected="selectedIds" :disabled="locked" :time-zone="site?.siteTimeZone ?? 'UTC'" show-device @toggle="toggle" @software="selectSoftware($event.software)" @open="openPanel" @grant="open('grant',null,$event)" @bind="open('assign',null,$event)" @revoke="open('revoke',null,$event)" /></template>
        <footer class="pagination"><span>已显示 {{ rows.length }} / {{ rowTotal }} 个位置<small v-if="readAt">读取时间 {{ new Date(readAt).toLocaleTimeString() }}</small></span><button v-if="rowNext" :disabled="reads.pending('rows')" @click="loadRows(rowNext)">加载更多软件位置</button></footer>
      </template><p v-else class="empty">{{ reads.pending('context') ? '正在读取完整资料…' : view==='software' ? '选择左侧软件，查看版本及它关联的设备。' : '暂无可展示的数据。' }}</p>
    </div>
  </div>
  <dialog ref="dialog" aria-labelledby="workspace-dialog-title" @cancel.prevent="closeDialog"><template v-if="draft"><div class="dialog-heading"><h2 id="workspace-dialog-title">{{ titles[draft.action] }}</h2><button aria-label="关闭" :disabled="locked" @click="closeDialog">×</button></div><p class="dialog-context">{{ draft.device?.name ?? draft.software?.name ?? draft.process?.name ?? site?.siteName }}<template v-if="draft.hardware"> / {{ draft.hardware.name }}</template><template v-if="draft.row"> / {{ draft.row.software.name }}</template></p>
    <form @submit.prevent="save"><fieldset :disabled="locked || !validDraft || !!issued">
      <template v-if="['editSoftware','process','editProcess','device','editDevice','hardware','editHardware'].includes(draft.action)"><label>名称<input v-model="name" required maxlength="128" /></label><p v-if="['process','device'].includes(draft.action)" class="muted">内部编号由系统生成。</p></template>
      <template v-if="['device','editDevice'].includes(draft.action)"><label>所属工序<select v-model="processChoice" required><option value="" disabled>请选择工序</option><option v-if="draft.device && !processes.some(p=>p.id===draft!.device!.processId)" :value="draft.device.processId">{{ draft.device.processName }}</option><option v-for="p in processes" :key="p.id" :value="p.id">{{ p.name }}</option></select></label><button v-if="processNext" type="button" @click="loadProcesses(processNext)">更多工序</button></template>
      <label v-if="draft.action==='editSoftware'">说明<textarea v-model="description" maxlength="2000" /></label><label v-if="['device','editDevice','hardware','editHardware'].includes(draft.action)">资产号（可选）<input v-model="assetNumber" maxlength="128" /></label>
      <label v-if="draft.action==='hardware'">硬件类型<select v-model="hardwareType"><option value="IPC">工控机</option><option value="PLC">PLC 控制器</option></select></label>
      <label v-if="['hardware','editHardware'].includes(draft.action)">管理 IP（可选）<input v-model="managementIp" maxlength="64" /><small>用于台账展示，与实例上报 IP 分开，不用于自动绑定。</small></label>
      <template v-if="draft.action==='associate'"><label>软件来源<select v-model="choice"><option value="existing">选择已有软件</option><option v-if="session.can('software.create')" value="new">新建软件</option></select></label>
        <template v-if="choice==='existing'"><div class="selector-search"><input v-model="candidateSearch" placeholder="搜索软件" aria-label="搜索待关联软件" /><button type="button" @click="loadCandidates()">查询</button></div><label>软件<select v-model="candidateId" required><option value="" disabled>选择有维护权限的软件</option><option v-for="s in candidates" :key="s.id" :value="s.id" :disabled="!session.can('instance.manage',s.id)">{{ s.name }} · {{ categoryName(s.category) }}{{ session.can('instance.manage',s.id) ? '' : '（无关联权限）' }}</option></select></label><button v-if="candidateNext" type="button" @click="loadCandidates(candidateNext)">更多软件</button></template>
        <template v-else><label>软件名称<input v-model="name" required maxlength="128" /></label><label>类型<select v-model="category"><option value="UpperComputer">上位机</option><option value="Vision">视觉</option></select></label><label>说明<textarea v-model="description" maxlength="2000" /></label><p class="muted">系统生成软件编号，并同时关联此工控机；GitLab 来源由部署配置维护。</p></template>
      </template>
      <template v-if="draft.action==='assign'"><p class="muted">仅补充同设备、同软件的空闲位置，已有身份、凭据和履历保持不变。</p><label>工控机<select v-model="positionChoice" required><option value="" disabled>选择空闲安装位置</option><option v-for="r in vacant" :key="r.rowId" :value="r.position!.id">{{ r.hardware?.name }}</option></select></label><button v-if="vacantNext" type="button" @click="loadVacant(vacantNext)">更多位置</button><p v-if="!vacant.length">暂无空闲位置，请先在本设备工控机下关联此软件。</p></template>
      <p v-if="draft.action==='revoke'">仅解除此工控机与软件的关联，保留关联历史。已有实例引用时服务器会拒绝解除。</p>
      <template v-if="draft.action==='grant'"><p>许可固定此设备、工控机及软件，仅可登记一个实例。</p><label>有效期（当前浏览器时区）<input v-model="expires" type="datetime-local" required /></label></template>
      <label v-if="!['process','device','hardware'].includes(draft.action)">操作原因<textarea v-model="reason" required maxlength="256" /></label>
    </fieldset><p v-if="!validDraft" class="error">人员或权限已变化，原草稿禁止继续。结果未知时仍请先核实原操作。</p>
    <div v-if="issued" class="grant-material"><h3>登记材料</h3><p>软件：{{ draft.row?.software.name }} / 工控机：{{ draft.hardware?.name }}</p><label>许可标识<input :value="issued.id" readonly /></label><label>一次显示的许可秘密<input :value="secret" readonly autocomplete="off" /></label><p class="muted">登记接口 /api/v1/enrollment/instances；softwareId：{{ draft.row?.software.id }}；deviceId：{{ draft.device?.id }}。凭据材料只留在本页内存，通过受控渠道交给接入方。</p></div>
    <p v-if="mutation.error.value" role="alert" class="error">{{ mutation.error.value }}</p><OperationStatus :mutation="mutation" /><div class="dialog-actions"><button type="button" :disabled="locked" @click="closeDialog">{{ issued ? '完成并清除秘密' : '取消' }}</button><button v-if="!issued" class="primary" :disabled="mutation.busy.value || !validDraft && !mutation.uncertain.value">{{ mutation.uncertain.value ? '核实原操作' : '确认' }}</button></div></form>
  </template></dialog>
</section>
</template>
