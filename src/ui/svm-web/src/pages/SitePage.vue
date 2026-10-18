<script setup lang="ts">
import { freshnessName, runningName, installationName, formatTime } from '../instances'
import { computed, nextTick, onMounted, ref } from 'vue'
import { ApiError, request } from '../api'
import { categoryName, type Binding, type Device, type Inventory, type Page, type Process, type Site, type Software } from '../catalog'
import { useProtectedMutation } from '../protectedMutation'
import { useSession } from '../session'
import OperationStatus from '../components/OperationStatus.vue'
import { useOwnedReads } from '../ownedReads'
const session = useSession(), mutation = useProtectedMutation()
const { busy, uncertain, blocked, error } = mutation
const site = ref<Site | null>(null), processes = ref<Process[]>([]), devices = ref<Device[]>([]), inventory = ref<Inventory[]>([]), bindings = ref<Binding[]>([])
const process = ref<Process | null>(null), device = ref<Device | null>(null), failure = ref(''), notice = ref('')
const processSearch = ref(''), deviceSearch = ref(''), inventoryCategory = ref('')
type PageKind = 'processes' | 'devices' | 'inventory' | 'bindings' | 'candidates'
const cursors = ref<Record<PageKind, string[]>>({ processes: [''], devices: [''], inventory: [''], bindings: [''], candidates: [''] })
const nextCursors = ref<Record<PageKind, string | null>>({ processes: null, devices: null, inventory: null, bindings: null, candidates: null })
type Mode = 'createProcess' | 'editProcess' | 'createDevice' | 'editDevice' | 'bind' | 'revoke'
const mode = ref<Mode | null>(null), dialog = ref<HTMLDialogElement | null>(null), code = ref(''), name = ref(''), reason = ref(''), targetId = ref('')
const targetBinding = ref<Binding | null>(null), candidates = ref<(Process | Software)[]>([]), candidateSearch = ref(''), candidateFailure = ref('')
const softwareNames = ref<Record<string, string>>({})
const titles: Record<Mode, string> = { createProcess: '创建工序', editProcess: '修改工序', createDevice: '创建设备', editDevice: '修改设备', bind: '建立设备软件映射', revoke: '撤销设备软件映射' }
const canMaintain = computed(() => session.can('asset.manage'))
const isDeviceMode = computed(() => mode.value === 'createDevice' || mode.value === 'editDevice')
const names = computed(() => ({ ...softwareNames.value, ...Object.fromEntries([...inventory.value.map(x => [x.software.id, `${x.software.name} · ${x.software.code}`]), ...candidates.value.filter(x => 'category' in x).map(x => [x.id, `${x.name} · ${x.code}`])]) }))
function reset(kind: PageKind) { cursors.value[kind] = ['']; nextCursors.value[kind] = null }
const applied=ref<Record<PageKind,Record<string,string>>>({processes:{},devices:{},inventory:{},bindings:{},candidates:{}})
const reads=useOwnedReads(()=>{site.value=null; processes.value=[]; process.value=null; devices.value=[]; device.value=null; inventory.value=[]; bindings.value=[]; candidates.value=[]; softwareNames.value={}; for(const key of Object.keys(cursors.value) as PageKind[]) reset(key)})
const loading=computed(()=>reads.pending('scene')), candidateLoading=computed(()=>reads.pending('candidates'))
const draft=ref<{process:Process|null; device:Device|null; epoch:number}|null>(null)
const draftValid=computed(()=>!!draft.value && draft.value.epoch===reads.epoch.value && canMaintain.value)
let returnFocus:HTMLElement|null=null
const get=<T,>(path:string,signal:AbortSignal)=>request<T>('/api/v1/manage/'+path,'GET',undefined,undefined,undefined,{signal})
function query(filters:Record<string,string>,stack:string[]) {const result=new URLSearchParams({pageSize:'50'}); for(const [key,value] of Object.entries(filters)) if(value) result.set(key,value); const cursor=stack[stack.length-1]; if(cursor) result.set('cursor',cursor); return result}
async function failed(e:unknown) {failure.value=e instanceof ApiError ? e.message : '现场台账读取失败，已保留原结果，请重试。'; if(e instanceof ApiError && [401,403,404].includes(e.status)) await session.load()}
function read<T>(path:string,filters:Record<string,string>,stack:string[],signal:AbortSignal) {return get<Page<T>>(path+'?'+query(filters,stack),signal)}
function commit<T>(kind:PageKind,p:Page<T>,filters:Record<string,string>,stack:string[]) {nextCursors.value[kind]=p.nextCursor; cursors.value[kind]=stack; applied.value[kind]=filters; return p.items}
function clearDevice() {device.value=null; inventory.value=[]; bindings.value=[]; reset('inventory'); reset('bindings')}
async function loadProcesses(refresh=false,stack=cursors.value.processes) {
  if(blocked.value || !session.canAssets) return
  const filters=refresh ? {name:processSearch.value} : {...applied.value.processes}, proposed=refresh ? [''] : [...stack]; failure.value=''
  await reads.run('scene',refresh,async signal=>{const [s,p]=await Promise.all([get<Site>('site',signal),read<Process>('processes',filters,proposed,signal)]); return {s,p}},({s,p})=>{site.value=s; processes.value=commit('processes',p,filters,proposed); if(refresh) {process.value=null; devices.value=[]; reset('devices'); clearDevice()}},failed)
}
async function selectProcess(id:string) {
  if(blocked.value || !session.canAssets) return
  failure.value=''
  await reads.run('scene',true,async signal=>{const [p,d]=await Promise.all([get<Process>('processes/'+id,signal),read<Device>('devices',{processId:id},[''],signal)]); return {p,d}},({p,d})=>{process.value=p; devices.value=commit('devices',d,{processId:id},['']); deviceSearch.value=''; clearDevice()},failed)
}
async function loadDevices(refresh=false,stack=cursors.value.devices) {
  if(!process.value || blocked.value || !session.canAssets) return
  const filters=refresh ? {processId:process.value.id,name:deviceSearch.value} : {...applied.value.devices}, proposed=refresh ? [''] : [...stack]; failure.value=''
  await reads.run('scene',refresh,signal=>read<Device>('devices',filters,proposed,signal),p=>{devices.value=commit('devices',p,filters,proposed); if(refresh) clearDevice()},failed)
}
async function selectDevice(id:string) {
  if(blocked.value || !session.canAssets) return
  failure.value=''
  await reads.run('scene',true,async signal=>{
    const [d,i,b]=await Promise.all([get<Device>('devices/'+id,signal),read<Inventory>(`devices/${id}/software-inventory`,{},[''],signal),read<Binding>(`devices/${id}/software-bindings`,{},[''],signal)])
    const labels=b.items.some(binding=>!i.items.some(x=>x.software.id===binding.softwareId) && !names.value[binding.softwareId]) ? await get<Page<Software>>('software?pageSize=50',signal) : {items:[]}
    return {d,i,b,labels}
  },({d,i,b,labels})=>{device.value=d; inventory.value=commit('inventory',i,{},['']); bindings.value=commit('bindings',b,{},['']); inventoryCategory.value=''; softwareNames.value={...softwareNames.value,...Object.fromEntries(labels.items.map(x=>[x.id,`${x.name} · ${x.code}`]))}},failed)
}
async function loadInventory(refresh=false,stack=cursors.value.inventory) {
  if(!device.value || blocked.value || !session.canAssets) return
  const id=device.value.id, filters=refresh ? {category:inventoryCategory.value} : {...applied.value.inventory}, proposed=refresh ? [''] : [...stack]; failure.value=''
  await reads.run('scene',refresh,signal=>read<Inventory>(`devices/${id}/software-inventory`,filters,proposed,signal),p=>{if(device.value?.id===id) inventory.value=commit('inventory',p,filters,proposed)},failed)
}
async function loadBindings(stack=cursors.value.bindings) {
  if(!device.value || blocked.value || !session.canAssets) return
  const id=device.value.id,proposed=[...stack]; failure.value=''
  await reads.run('scene',false,signal=>read<Binding>(`devices/${id}/software-bindings`,{},proposed,signal),p=>{if(device.value?.id===id) bindings.value=commit('bindings',p,{},proposed)},failed)
}
async function loadCandidates(refresh=false,stack=cursors.value.candidates) {
  if(!mode.value || !draftValid.value || blocked.value) return
  const filters=refresh ? {name:candidateSearch.value} : {...applied.value.candidates}, proposed=refresh ? [''] : [...stack], path=mode.value==='bind' ? 'software' : 'processes'; candidateFailure.value=''
  await reads.run('candidates',refresh,signal=>read<Process|Software>(path,filters,proposed,signal),p=>{candidates.value=commit('candidates',p,filters,proposed)},e=>{candidateFailure.value=e instanceof ApiError ? e.message : '候选读取失败，已保留原结果。'})
}
async function page(kind:PageKind,forward:boolean) {
  const stack=cursors.value[kind], cursor=nextCursors.value[kind]
  if(forward && !cursor || !forward && stack.length<=1 || blocked.value) return
  const proposed=forward ? [...stack,cursor!] : stack.slice(0,-1)
  if(kind==='processes') await loadProcesses(false,proposed); else if(kind==='devices') await loadDevices(false,proposed); else if(kind==='inventory') await loadInventory(false,proposed); else if(kind==='bindings') await loadBindings(proposed); else await loadCandidates(false,proposed)
}
async function open(nextMode: Mode, binding?: Binding) {
  if (blocked.value || loading.value) return
  if (nextMode === 'editProcess' && !process.value || nextMode === 'editDevice' && !device.value) return
  draft.value={process:process.value ? {...process.value} : null,device:device.value ? {...device.value} : null,epoch:reads.epoch.value}; mode.value = nextMode; targetBinding.value = binding ? {...binding} : null; code.value = ''; reason.value = ''; error.value = ''
  name.value = nextMode === 'editProcess' ? process.value!.name : nextMode === 'editDevice' ? device.value!.name : ''
  targetId.value = isDeviceMode.value ? device.value?.processId ?? process.value?.id ?? '' : ''
  candidateSearch.value = ''; candidates.value = []; reset('candidates')
  returnFocus=document.activeElement as HTMLElement|null; await nextTick(); dialog.value?.showModal()
  if (isDeviceMode.value || nextMode === 'bind') await loadCandidates(true)
}
function close() {if(!blocked.value) {reads.invalidate('candidates','draft'); dialog.value?.close(); mode.value=null; draft.value=null; error.value=''; returnFocus?.focus()}}
async function refreshDraft() {
  const d=draft.value; if(blocked.value || !d || !draftValid.value) return
  if(mode.value==='editProcess' && d.process) await reads.run('draft',true,signal=>get<Process>('processes/'+d.process!.id,signal),p=>{d.process=p; name.value=p.name; reason.value=''; error.value=''},failed)
  else if(mode.value==='editDevice' && d.device) await reads.run('draft',true,signal=>get<Device>('devices/'+d.device!.id,signal),v=>{d.device=v; name.value=v.name; targetId.value=v.processId; reason.value=''; error.value=''},failed)
  else {error.value='请关闭表单，重新读取关联后再操作。'}
}
async function save() {
  const d=draft.value; if(!d || !uncertain.value && !draftValid.value) {error.value='人员或权限已变化，请关闭表单并重新核对。'; return}
  let path = '/api/v1/manage/', method = 'POST', body: object
  if (mode.value === 'createProcess') { path += 'processes'; body = { code: code.value, name: name.value } }
  else if (mode.value === 'editProcess') { path += 'processes/' + d.process!.id; method = 'PATCH'; body = { name: name.value, expectedRevision: d.process!.revision, reason: reason.value } }
  else if (mode.value === 'createDevice') { path += 'devices'; body = { deviceNo: code.value, name: name.value, processId: targetId.value } }
  else if (mode.value === 'editDevice') { path += 'devices/' + d.device!.id; method = 'PATCH'; body = { name: name.value, processId: targetId.value, expectedRevision: d.device!.revision, reason: reason.value } }
  else if (mode.value === 'bind') { path += `devices/${d.device!.id}/software-bindings`; body = { softwareId: targetId.value, reason: reason.value } }
  else { if (!targetBinding.value) { error.value = '关联已变化，请关闭表单并重新读取。'; return }; path += `devices/${d.device!.id}/software-bindings/${targetBinding.value.softwareId}`; method = 'DELETE'; body = { expectedRevision: targetBinding.value.revision, reason: reason.value } }
  const currentMode = mode.value, currentProcess = d.process?.id, currentDevice = d.device?.id
  const result = await mutation.perform<Process | Device | Binding>(path, method, body, session.current?.csrfToken ?? '')
  if (mutation.completed.value) {
    dialog.value?.close(); mode.value = null; notice.value = '台账操作已完成。'; await session.load()
    if (!session.canAssets) return
    if (currentMode === 'createProcess' || currentMode === 'editProcess') { await loadProcesses(); await selectProcess((result as Process).id) }
    else if (currentMode === 'createDevice' || currentMode === 'editDevice') { const value = result as Device; await selectProcess(value.processId); await selectDevice(value.id) }
    else if (currentDevice) await selectDevice(currentDevice)
    else if (currentProcess) await selectProcess(currentProcess)
  } else if (!uncertain.value) await session.load()
}
onMounted(() => loadProcesses(true))
</script>
<template>
  <section class="workspace">
    <div class="page-heading"><div><div class="eyebrow">现场管理</div><h1>现场台账</h1><p class="muted">按工序和设备选择对应软件。</p></div><button v-if="site && canMaintain" class="primary" :disabled="blocked || loading" @click="open('createProcess')">创建工序</button></div>
    <p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p>
    <div v-if="!site" class="panel empty"><p>{{ loading ? '正在读取厂区配置…' : '现场台账暂不可用。请确认本部署的厂区配置与访问权限。' }}</p><button v-if="!loading" @click="loadProcesses(true)">重新读取厂区</button></div>
    <template v-else>
      <nav class="breadcrumbs" aria-label="现场层级"><strong>{{ site.siteName }}</strong><span>›</span><span>{{ process?.name ?? '选择工序' }}</span><span>›</span><span>{{ device?.name ?? '选择设备' }}</span><span>›</span><span>对应软件</span><small>{{ site.siteTimeZone }}</small></nav>
      <div class="site-layout">
        <section class="panel"><div class="section-heading"><h2>工序</h2><button v-if="process && canMaintain" :disabled="loading || blocked" @click="open('editProcess')">修改工序</button></div><form class="compact-filters" @submit.prevent="loadProcesses(true)"><label>查找工序<input v-model="processSearch" maxlength="128" /></label><button :disabled="loading || blocked">查询</button></form>
          <div v-if="!processes.length" class="empty">{{ loading ? '正在读取…' : '没有符合条件的工序。' }}</div><ul class="selection-list"><li v-for="item in processes" :key="item.id"><button :class="{ active: process?.id === item.id }" :disabled="loading || blocked" @click="selectProcess(item.id)">{{ item.name }}<small>{{ item.code }}</small></button></li></ul><footer class="pagination"><span>本页 {{ processes.length }} 项</span><div><button :disabled="loading || blocked || cursors.processes.length <= 1" @click="page('processes', false)">上一页</button><button :disabled="loading || blocked || !nextCursors.processes" @click="page('processes', true)">下一页</button></div></footer>
        </section>
        <section class="panel"><div class="section-heading"><h2>设备</h2><button v-if="process && canMaintain" :disabled="loading || blocked" @click="open('createDevice')">创建设备</button></div><template v-if="process"><form class="compact-filters" @submit.prevent="loadDevices(true)"><label>查找设备<input v-model="deviceSearch" maxlength="128" /></label><button :disabled="loading || blocked">查询</button></form><div v-if="!devices.length" class="empty">{{ loading ? '正在读取…' : '没有符合条件的设备。' }}</div><ul class="selection-list"><li v-for="item in devices" :key="item.id"><button :class="{ active: device?.id === item.id }" :disabled="loading || blocked" @click="selectDevice(item.id)">{{ item.name }}<small>{{ item.deviceNo }}</small></button></li></ul><footer class="pagination"><span>本页 {{ devices.length }} 台</span><div><button :disabled="loading || blocked || cursors.devices.length <= 1" @click="page('devices', false)">上一页</button><button :disabled="loading || blocked || !nextCursors.devices" @click="page('devices', true)">下一页</button></div></footer></template><div v-else class="empty">先选择工序。</div></section>
        <section class="panel device-detail"><template v-if="device"><div class="section-heading"><div><div class="eyebrow">设备详情</div><h2>{{ device.name }}</h2></div><button v-if="canMaintain" :disabled="loading || blocked" @click="open('editDevice')">修改设备</button></div><dl class="device-facts"><div><dt>设备编号</dt><dd>{{ device.deviceNo }}</dd></div><div><dt>工序</dt><dd>{{ device.processName }}</dd></div><div><dt>资料修订</dt><dd>{{ device.revision }}</dd></div></dl>
          <form class="compact-filters" @submit.prevent="loadInventory(true)"><label>对应软件分类<select v-model="inventoryCategory" aria-label="对应软件分类"><option value="">全部分类</option><option value="UpperComputer">上位机</option><option value="Vision">视觉</option></select></label><button :disabled="loading || blocked">筛选软件</button></form><div v-if="!inventory.length" class="empty">{{ loading ? '正在读取…' : '没有可查看的设备软件。请核对设备映射与软件查看权限。' }}</div>
          <article v-for="item in inventory" :key="item.instance?.id ?? item.software.id" class="inventory-card"><div><h3>{{ item.software.name }}</h3><p class="muted">{{ item.software.code }} · {{ categoryName(item.software.category) }}</p></div><span class="badge" :class="{ disabled: !item.instance || item.instance.freshness !== 'Fresh' }">{{ item.instance ? freshnessName(item.instance.freshness) : '尚未登记' }}</span><p v-if="item.software.description" class="description">{{ item.software.description }}</p>
            <template v-if="item.instance"><dl class="device-facts"><div><dt>实际安装</dt><dd>{{ installationName(item.instance.lastSnapshot?.installationState) }} · {{ item.instance.lastSnapshot?.installedVersion ?? '—' }}</dd></div><div><dt>运行状态</dt><dd>{{ runningName(item.instance.lastSnapshot?.runningState) }}</dd></div><div><dt>上报 IP</dt><dd>{{ item.instance.lastSnapshot?.reportedIps.join('、') || '—' }}</dd></div><div><dt>API 接入</dt><dd>{{ item.instance.lifecycle === 'Active' ? '启用' : '暂停' }}</dd></div><div><dt>最后上报</dt><dd>{{ item.instance.lastAcceptedAt ? formatTime(item.instance.lastAcceptedAt, site?.siteTimeZone) : '尚未上报' }}</dd></div><div><dt>未上报时长</dt><dd>{{ item.instance.unreportedSeconds === null ? '—' : item.instance.unreportedSeconds + ' 秒' }}</dd></div></dl><p v-if="item.instance.lastSnapshot?.installedVersion && !item.instance.lastSnapshot.installedReleaseId" class="muted">现场版本未关联平台发布记录。</p><RouterLink class="text-link" :to="{ path: '/instances', query: { softwareId: item.software.id, id: item.instance.id } }">实例详情与安装履历</RouterLink></template>
            <p v-else class="muted">映射已建立，等待现场软件登记。</p><RouterLink v-if="session.can('deployment.create',item.software.id) || session.can('instance.read',item.software.id)" class="text-link" :to="{ path: '/deployments', query: { softwareId:item.software.id, deviceId:device.id } }">更新投放与任务</RouterLink><p v-if="item.instance?.latestTaskId" class="muted">最近任务 {{ item.instance.latestTaskId }} · {{ item.instance.latestTaskResult ?? '尚无终态' }}</p><RouterLink class="text-link" :to="{ path: '/releases', query: { softwareId: item.software.id, deviceId: device.id } }">版本与安装包</RouterLink><RouterLink v-if="item.software.latestAvailableFormalReleaseId" class="text-link" :to="{ path: '/releases', query: { softwareId: item.software.id, channel: 'Formal', releaseId: item.software.latestAvailableFormalReleaseId } }">最新可用正式版</RouterLink><span v-else class="muted">尚无可用正式版</span><RouterLink v-if="session.can('enrollment.manage', item.software.id)" class="text-link" :to="{ path: '/enrollment', query: { softwareId: item.software.id, deviceId: device.id } }">维护登记许可</RouterLink>
          </article>
          <footer class="pagination"><span>本页可查看 {{ inventory.length }} 项</span><div><button :disabled="loading || blocked || cursors.inventory.length <= 1" @click="page('inventory', false)">上一页软件</button><button :disabled="loading || blocked || !nextCursors.inventory" @click="page('inventory', true)">下一页软件</button></div></footer>
          <section v-if="session.can('asset.read')" class="mapping-section"><div class="section-heading"><h3>设备软件映射</h3><button v-if="canMaintain" :disabled="loading || blocked" @click="open('bind')">关联软件</button></div><p class="muted">只显示有软件查看权限的关联；撤销另需此软件的映射维护权限。</p><ul class="permission-list"><li v-for="binding in bindings" :key="binding.softwareId">{{ names[binding.softwareId] ?? binding.softwareId }}<small>关联修订 {{ binding.revision }}</small><RouterLink class="text-link" :to="{ path: '/software', query: { id: binding.softwareId } }">查看软件资料</RouterLink><button v-if="canMaintain && session.can('instance.manage', binding.softwareId)" class="text-button" :disabled="loading || blocked" @click="open('revoke', binding)">撤销映射</button></li></ul><p v-if="!bindings.length" class="muted">没有可查看的关联。</p><div class="pager-inline"><button :disabled="loading || blocked || cursors.bindings.length <= 1" @click="page('bindings', false)">上一页关联</button><button :disabled="loading || blocked || !nextCursors.bindings" @click="page('bindings', true)">下一页关联</button></div></section>
        </template><div v-else class="empty">选择设备，查看对应软件。</div></section>
      </div>
    </template>
    <dialog v-if="mode" ref="dialog" @cancel.prevent="close" aria-labelledby="site-dialog-title"><div class="dialog-heading"><h2 id="site-dialog-title">{{ titles[mode] }}</h2><button :disabled="blocked" @click="close" aria-label="关闭">×</button></div><form @submit.prevent="save"><p v-if="draft" class="muted">操作对象：{{ draft.device?.name ?? draft.process?.name ?? site?.siteName }} · {{ draft.device?.id ?? draft.process?.id ?? site?.siteId }}</p><p v-if="!draftValid" class="error">人员或权限已变化，请关闭表单并重新核对。</p><fieldset :disabled="blocked || !draftValid">
      <label v-if="mode === 'createProcess' || mode === 'createDevice'">{{ mode === 'createProcess' ? '工序代码' : '设备编号' }}<input v-model="code" required maxlength="64" /></label><label v-if="mode !== 'bind' && mode !== 'revoke'">{{ isDeviceMode ? '设备名称' : '工序名称' }}<input v-model="name" required maxlength="128" /></label>
      <template v-if="isDeviceMode || mode === 'bind'"><div class="compact-filters"><label>候选名称<input v-model="candidateSearch" maxlength="128" /></label><button type="button" :disabled="candidateLoading" @click="loadCandidates(true)">查找候选</button></div><p v-if="candidateFailure" class="error" role="alert">{{ candidateFailure }}</p><label>{{ mode === 'bind' ? '选择关联软件' : '所属工序' }}<select v-model="targetId" :aria-label="mode === 'bind' ? '选择关联软件' : '所属工序'" required><option value="">请选择</option><option v-if="isDeviceMode && targetId && !candidates.some(c => c.id === targetId)" :value="targetId">{{ device?.processName ?? process?.name }}（当前工序）</option><option v-for="candidate in candidates" :key="candidate.id" :value="candidate.id" :disabled="mode === 'bind' && !session.can('instance.manage', candidate.id)">{{ candidate.name }} · {{ candidate.code }}{{ 'category' in candidate ? ' · ' + categoryName(candidate.category) : '' }}</option></select></label><div class="pager-inline"><button type="button" :disabled="candidateLoading || cursors.candidates.length <= 1" @click="page('candidates', false)">上一页候选</button><button type="button" :disabled="candidateLoading || !nextCursors.candidates" @click="page('candidates', true)">下一页候选</button></div><p v-if="mode === 'bind'" class="muted">需具备此软件的查看与映射维护权限。关联后，设备软件仍显示“尚未登记”。</p></template>
      <p v-if="mode === 'revoke'" class="muted">{{ device?.name }} · {{ targetBinding ? names[targetBinding.softwareId] ?? targetBinding.softwareId : '关联已变化' }}。已确认实例引用的关联不可撤销。</p><label v-if="mode !== 'createProcess' && mode !== 'createDevice'">操作原因<textarea v-model="reason" required maxlength="256" rows="2" /></label>
      </fieldset><p v-if="error" class="error" role="alert">{{ error }}</p><OperationStatus :mutation="mutation" /><p v-if="uncertain" class="muted">结果待核实。原请求与操作键已保留，请手动核实。</p><div class="dialog-actions"><button v-if="error && !uncertain && mode !== 'createProcess' && mode !== 'createDevice' && mode !== 'bind'" type="button" @click="refreshDraft">加载最新详情</button><button v-if="!uncertain" type="button" :disabled="busy" @click="close">取消</button><button class="primary" :disabled="busy || (!uncertain && !draftValid)">{{ busy ? '正在提交…' : uncertain ? '核实原操作' : mode === 'revoke' ? '确认撤销' : '保存' }}</button></div></form></dialog>
  </section>
</template>
