<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { ApiError, request } from '../api'
import { categoryName, type Binding, type Device, type Inventory, type Page, type Process, type Site, type Software } from '../catalog'
import { useProtectedMutation } from '../protectedMutation'
import { useSession } from '../session'
const session = useSession(), mutation = useProtectedMutation()
const { busy, uncertain, blocked, error } = mutation
const site = ref<Site | null>(null), processes = ref<Process[]>([]), devices = ref<Device[]>([]), inventory = ref<Inventory[]>([]), bindings = ref<Binding[]>([])
const process = ref<Process | null>(null), device = ref<Device | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const processSearch = ref(''), deviceSearch = ref(''), inventoryCategory = ref('')
type PageKind = 'processes' | 'devices' | 'inventory' | 'bindings' | 'candidates'
const cursors = ref<Record<PageKind, string[]>>({ processes: [''], devices: [''], inventory: [''], bindings: [''], candidates: [''] })
const nextCursors = ref<Record<PageKind, string | null>>({ processes: null, devices: null, inventory: null, bindings: null, candidates: null })
type Mode = 'createProcess' | 'editProcess' | 'createDevice' | 'editDevice' | 'bind' | 'revoke'
const mode = ref<Mode | null>(null), dialog = ref<HTMLDialogElement | null>(null), code = ref(''), name = ref(''), reason = ref(''), targetId = ref('')
const targetBinding = ref<Binding | null>(null), candidates = ref<(Process | Software)[]>([]), candidateSearch = ref(''), candidateFailure = ref(''), candidateLoading = ref(false)
const softwareNames = ref<Record<string, string>>({})
const titles: Record<Mode, string> = { createProcess: '创建工序', editProcess: '修改工序', createDevice: '创建设备', editDevice: '修改设备', bind: '建立设备软件映射', revoke: '撤销设备软件映射' }
const canMaintain = computed(() => session.can('asset.manage'))
const isDeviceMode = computed(() => mode.value === 'createDevice' || mode.value === 'editDevice')
const names = computed(() => ({ ...softwareNames.value, ...Object.fromEntries([...inventory.value.map(x => [x.software.id, `${x.software.name} · ${x.software.code}`]), ...candidates.value.filter(x => 'category' in x).map(x => [x.id, `${x.name} · ${x.code}`])]) }))
function reset(kind: PageKind) { cursors.value[kind] = ['']; nextCursors.value[kind] = null }
function query(kind: PageKind, filters: Record<string, string> = {}) {
  const result = new URLSearchParams({ pageSize: '50' }); for (const [key, value] of Object.entries(filters)) if (value) result.set(key, value)
  const cursor = cursors.value[kind][cursors.value[kind].length - 1]; if (cursor) result.set('cursor', cursor); return result
}
async function failed(e: unknown) {
  failure.value = e instanceof ApiError ? e.message : '现场台账读取失败。'
  if (e instanceof ApiError && [401, 403, 404].includes(e.status)) await session.load()
}
async function read<T>(kind: PageKind, path: string, filters: Record<string, string> = {}) {
  const page = await request<Page<T>>('/api/v1/manage/' + path + '?' + query(kind, filters)); nextCursors.value[kind] = page.nextCursor; return page.items
}
async function loadProcesses(refresh = false) {
  if (refresh) { reset('processes'); process.value = null; device.value = null; devices.value = []; inventory.value = []; bindings.value = [] }
  loading.value = true; failure.value = ''
  try { site.value = await request<Site>('/api/v1/manage/site'); processes.value = await read<Process>('processes', 'processes', { name: processSearch.value }) }
  catch (e) { site.value = null; processes.value = []; await failed(e) }
  finally { loading.value = false }
}
async function selectProcess(id: string) {
  loading.value = true; failure.value = ''; device.value = null; inventory.value = []; bindings.value = []; reset('devices'); deviceSearch.value = ''
  try { process.value = await request<Process>('/api/v1/manage/processes/' + id); devices.value = await read<Device>('devices', 'devices', { processId: id }) }
  catch (e) { process.value = null; devices.value = []; await failed(e) }
  finally { loading.value = false }
}
async function loadDevices(refresh = false) {
  if (!process.value) return
  if (refresh) { reset('devices'); device.value = null; inventory.value = []; bindings.value = [] }
  loading.value = true; failure.value = ''
  try { devices.value = await read<Device>('devices', 'devices', { processId: process.value.id, name: deviceSearch.value }) }
  catch (e) { devices.value = []; await failed(e) }
  finally { loading.value = false }
}
async function selectDevice(id: string) {
  loading.value = true; failure.value = ''; reset('inventory'); reset('bindings'); inventoryCategory.value = ''
  try {
    device.value = await request<Device>('/api/v1/manage/devices/' + id); inventory.value = await read<Inventory>('inventory', `devices/${id}/software-inventory`); bindings.value = await read<Binding>('bindings', `devices/${id}/software-bindings`)
    // One bounded catalog page supplies labels without querying once for each mapping.
    // A user can open an uncached software's detail through the auxiliary catalog.
    if (bindings.value.some(b => !names.value[b.softwareId])) {
      const page = await request<Page<Software>>('/api/v1/manage/software?pageSize=50')
      softwareNames.value = { ...softwareNames.value, ...Object.fromEntries(page.items.map(s => [s.id, `${s.name} · ${s.code}`])) }
    }
  }
  catch (e) { device.value = null; inventory.value = []; bindings.value = []; await failed(e) }
  finally { loading.value = false }
}
async function loadInventory(refresh = false) {
  if (!device.value) return
  if (refresh) reset('inventory')
  loading.value = true; failure.value = ''
  try { inventory.value = await read<Inventory>('inventory', `devices/${device.value.id}/software-inventory`, { category: inventoryCategory.value }) }
  catch (e) { inventory.value = []; await failed(e) }
  finally { loading.value = false }
}
async function loadBindings() {
  if (!device.value) return
  loading.value = true; failure.value = ''
  try { bindings.value = await read<Binding>('bindings', `devices/${device.value.id}/software-bindings`) }
  catch (e) { bindings.value = []; await failed(e) }
  finally { loading.value = false }
}
async function loadCandidates(refresh = false) {
  if (refresh) reset('candidates')
  candidateLoading.value = true; candidateFailure.value = ''
  try { candidates.value = await read<Process | Software>('candidates', mode.value === 'bind' ? 'software' : 'processes', { name: candidateSearch.value }) }
  catch (e) { candidates.value = []; candidateFailure.value = e instanceof ApiError ? e.message : '候选读取失败。' }
  finally { candidateLoading.value = false }
}
async function page(kind: PageKind, forward: boolean) {
  if (forward) { const next = nextCursors.value[kind]; if (!next) return; cursors.value[kind].push(next) }
  else { if (cursors.value[kind].length <= 1) return; cursors.value[kind].pop() }
  if (kind === 'processes') await loadProcesses(); else if (kind === 'devices') await loadDevices(); else if (kind === 'inventory') await loadInventory(); else if (kind === 'bindings') await loadBindings(); else await loadCandidates()
}
async function open(nextMode: Mode, binding?: Binding) {
  if (blocked.value || loading.value) return
  if (nextMode === 'editProcess' && process.value) process.value = await request<Process>('/api/v1/manage/processes/' + process.value.id).catch(e => { void failed(e); return null })
  if (nextMode === 'editDevice' && device.value) device.value = await request<Device>('/api/v1/manage/devices/' + device.value.id).catch(e => { void failed(e); return null })
  if (nextMode === 'editProcess' && !process.value || nextMode === 'editDevice' && !device.value) return
  mode.value = nextMode; targetBinding.value = binding ?? null; code.value = ''; reason.value = ''; error.value = ''
  name.value = nextMode === 'editProcess' ? process.value!.name : nextMode === 'editDevice' ? device.value!.name : ''
  targetId.value = isDeviceMode.value ? device.value?.processId ?? process.value?.id ?? '' : ''
  candidateSearch.value = ''; candidates.value = []; reset('candidates')
  await nextTick(); dialog.value?.showModal()
  if (isDeviceMode.value || nextMode === 'bind') await loadCandidates()
}
function close() { if (!blocked.value) { dialog.value?.close(); mode.value = null; error.value = '' } }
async function refreshDraft() {
  if (blocked.value) return
  if (mode.value === 'editProcess' && process.value) { await selectProcess(process.value.id); if (process.value) name.value = process.value.name }
  else if (mode.value === 'editDevice' && device.value) { const id = device.value.id; await selectDevice(id); if (device.value) { name.value = device.value.name; targetId.value = device.value.processId } }
  else if (mode.value === 'revoke' && device.value) { reset('bindings'); await loadBindings(); targetBinding.value = bindings.value.find(b => b.softwareId === targetBinding.value?.softwareId) ?? null }
  reason.value = ''; error.value = ''
}
async function save() {
  let path = '/api/v1/manage/', method = 'POST', body: object
  if (mode.value === 'createProcess') { path += 'processes'; body = { code: code.value, name: name.value } }
  else if (mode.value === 'editProcess') { path += 'processes/' + process.value!.id; method = 'PATCH'; body = { name: name.value, expectedRevision: process.value!.revision, reason: reason.value } }
  else if (mode.value === 'createDevice') { path += 'devices'; body = { deviceNo: code.value, name: name.value, processId: targetId.value } }
  else if (mode.value === 'editDevice') { path += 'devices/' + device.value!.id; method = 'PATCH'; body = { name: name.value, processId: targetId.value, expectedRevision: device.value!.revision, reason: reason.value } }
  else if (mode.value === 'bind') { path += `devices/${device.value!.id}/software-bindings`; body = { softwareId: targetId.value, reason: reason.value } }
  else { if (!targetBinding.value) { error.value = '关联已变化，请关闭表单并重新读取。'; return }; path += `devices/${device.value!.id}/software-bindings/${targetBinding.value.softwareId}`; method = 'DELETE'; body = { expectedRevision: targetBinding.value.revision, reason: reason.value } }
  const currentMode = mode.value, currentProcess = process.value?.id, currentDevice = device.value?.id
  const result = await mutation.perform<Process | Device | Binding>(path, method, body, session.current!.csrfToken)
  if (mutation.completed.value) {
    dialog.value?.close(); mode.value = null; notice.value = '台账操作已完成。'; await session.load()
    if (!session.canAssets) return
    if (currentMode === 'createProcess' || currentMode === 'editProcess') { await loadProcesses(); await selectProcess((result as Process).id) }
    else if (currentMode === 'createDevice' || currentMode === 'editDevice') { const value = result as Device; await selectProcess(value.processId); await selectDevice(value.id) }
    else if (currentDevice) await selectDevice(currentDevice)
    else if (currentProcess) await selectProcess(currentProcess)
  } else if (!uncertain.value) await session.load()
}
onMounted(() => loadProcesses())
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
          <article v-for="item in inventory" :key="item.software.id" class="inventory-card"><div><h3>{{ item.software.name }}</h3><p class="muted">{{ item.software.code }} · {{ categoryName(item.software.category) }}</p></div><span class="badge disabled">尚未登记</span><p v-if="item.software.description" class="description">{{ item.software.description }}</p><p class="muted">关联修订 {{ item.binding.revision }}。登记后才能取得安装版本、IP、启用状态与运行上报。</p></article>
          <footer class="pagination"><span>本页可查看 {{ inventory.length }} 项</span><div><button :disabled="loading || blocked || cursors.inventory.length <= 1" @click="page('inventory', false)">上一页软件</button><button :disabled="loading || blocked || !nextCursors.inventory" @click="page('inventory', true)">下一页软件</button></div></footer>
          <section v-if="session.can('asset.read')" class="mapping-section"><div class="section-heading"><h3>设备软件映射</h3><button v-if="canMaintain" :disabled="loading || blocked" @click="open('bind')">关联软件</button></div><p class="muted">只显示有软件查看权限的关联；撤销另需此软件的映射维护权限。</p><ul class="permission-list"><li v-for="binding in bindings" :key="binding.softwareId">{{ names[binding.softwareId] ?? binding.softwareId }}<small>关联修订 {{ binding.revision }}</small><RouterLink class="text-link" :to="{ path: '/software', query: { id: binding.softwareId } }">查看软件资料</RouterLink><button v-if="canMaintain && session.can('instance.manage', binding.softwareId)" class="text-button" :disabled="loading || blocked" @click="open('revoke', binding)">撤销映射</button></li></ul><p v-if="!bindings.length" class="muted">没有可查看的关联。</p><div class="pager-inline"><button :disabled="loading || blocked || cursors.bindings.length <= 1" @click="page('bindings', false)">上一页关联</button><button :disabled="loading || blocked || !nextCursors.bindings" @click="page('bindings', true)">下一页关联</button></div></section>
        </template><div v-else class="empty">选择设备，查看对应软件。</div></section>
      </div>
    </template>
    <dialog v-if="mode" ref="dialog" @cancel.prevent="close" aria-labelledby="site-dialog-title"><div class="dialog-heading"><h2 id="site-dialog-title">{{ titles[mode] }}</h2><button :disabled="blocked" @click="close" aria-label="关闭">×</button></div><form @submit.prevent="save"><fieldset :disabled="blocked">
      <label v-if="mode === 'createProcess' || mode === 'createDevice'">{{ mode === 'createProcess' ? '工序代码' : '设备编号' }}<input v-model="code" required maxlength="64" /></label><label v-if="mode !== 'bind' && mode !== 'revoke'">{{ isDeviceMode ? '设备名称' : '工序名称' }}<input v-model="name" required maxlength="128" /></label>
      <template v-if="isDeviceMode || mode === 'bind'"><div class="compact-filters"><label>候选名称<input v-model="candidateSearch" maxlength="128" /></label><button type="button" :disabled="candidateLoading" @click="loadCandidates(true)">查找候选</button></div><p v-if="candidateFailure" class="error" role="alert">{{ candidateFailure }}</p><label>{{ mode === 'bind' ? '选择关联软件' : '所属工序' }}<select v-model="targetId" :aria-label="mode === 'bind' ? '选择关联软件' : '所属工序'" required><option value="">请选择</option><option v-if="isDeviceMode && targetId && !candidates.some(c => c.id === targetId)" :value="targetId">{{ device?.processName ?? process?.name }}（当前工序）</option><option v-for="candidate in candidates" :key="candidate.id" :value="candidate.id" :disabled="mode === 'bind' && !session.can('instance.manage', candidate.id)">{{ candidate.name }} · {{ candidate.code }}{{ 'category' in candidate ? ' · ' + categoryName(candidate.category) : '' }}</option></select></label><div class="pager-inline"><button type="button" :disabled="candidateLoading || cursors.candidates.length <= 1" @click="page('candidates', false)">上一页候选</button><button type="button" :disabled="candidateLoading || !nextCursors.candidates" @click="page('candidates', true)">下一页候选</button></div><p v-if="mode === 'bind'" class="muted">需具备此软件的查看与映射维护权限。关联后，设备软件仍显示“尚未登记”。</p></template>
      <p v-if="mode === 'revoke'" class="muted">{{ device?.name }} · {{ targetBinding ? names[targetBinding.softwareId] ?? targetBinding.softwareId : '关联已变化' }}。已确认实例引用的关联不可撤销。</p><label v-if="mode !== 'createProcess' && mode !== 'createDevice'">操作原因<textarea v-model="reason" required maxlength="256" rows="2" /></label>
      </fieldset><p v-if="error" class="error" role="alert">{{ error }}</p><p v-if="uncertain" class="muted">结果待核实。原请求与操作键已保留，请手动核实。</p><div class="dialog-actions"><button v-if="error && !uncertain && mode !== 'createProcess' && mode !== 'createDevice' && mode !== 'bind'" type="button" @click="refreshDraft">加载最新详情</button><button v-if="!uncertain" type="button" :disabled="busy" @click="close">取消</button><button class="primary" :disabled="busy">{{ busy ? '正在提交…' : uncertain ? '核实原操作' : mode === 'revoke' ? '确认撤销' : '保存' }}</button></div></form></dialog>
  </section>
</template>
