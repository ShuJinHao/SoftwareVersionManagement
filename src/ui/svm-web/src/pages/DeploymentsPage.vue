<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { request, ApiError } from '../api'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
import { type Page } from '../catalog'
import { type Instance, formatTime } from '../instances'
import { type Release } from '../packages'
import { type Selection, type Deployment, type Batch, type InstanceTask, type Admission, type Receipt, type IntegrationMaterial, type DeploymentCapabilities, type TaskWork, type ControlItem, taskEnded, taskState, waitReason, admissionReason, utcInput, lines } from '../tasks'

const route = useRoute(), session = useSession(), mutation = useProtectedMutation()
const { busy, uncertain, error } = mutation
const softwareId = typeof route.query.softwareId === 'string' ? route.query.softwareId : ''
const selectedReleaseId = ref(typeof route.query.releaseId === 'string' ? route.query.releaseId : '')
const canCreate = computed(() => session.can('deployment.create', softwareId)), canRead = computed(() => session.can('instance.read', softwareId)), canControl = computed(() => session.can('deployment.control', softwareId))
const caps = ref<DeploymentCapabilities | null>(null), releases = ref<Release[]>([]), instances = ref<Instance[]>([]), deployments = ref<Deployment[]>([])
const selected = ref<Deployment | null>(null), batches = ref<Batch[]>([]), tasks = ref<InstanceTask[]>([]), admissions = ref<Admission[]>([]), receipts = ref<Receipt[]>([]), receiptTask = ref('')
const batchNext = ref<string | null>(null), controlWork = ref<TaskWork | null>(null), controlItems = ref<ControlItem[]>([]), controlNext = ref<string | null>(null)
const instanceNext = ref<string | null>(null), deploymentNext = ref<string | null>(null), taskNext = ref<string | null>(null), admissionNext = ref<string | null>(null), receiptNext = ref<string | null>(null), releaseNext = ref<string | null>(null)
const loading = ref(false), failure = ref(''), notice = ref(''), launchOpen = ref(false), mode = ref('Explicit')
const filter = ref({ processId: '', deviceId: typeof route.query.deviceId === 'string' ? route.query.deviceId : '', deviceNo: '', reportedIp: '', installedReleaseId: '', freshness: '', runningState: '', lifecycle: '' })
const chosen = ref(new Set<string>()), reason = ref(''), useDefaultWindow = ref(true), start = ref(''), end = ref(''), retryOf = ref<string | null>(null)
const selection = ref<Selection | null>(null), launchStage = ref<'selection' | 'chunks' | 'seal' | 'deployment'>('selection'), chunk = ref(0), launchIds = ref<string[]>([])
const launchActive = ref(false), pendingAction = ref<'launch' | 'control' | 'material' | null>(null)
const controlReason = ref(''), controlStart = ref(''), controlEnd = ref(''), onsite = ref(''), checkedOnsite = ref(false), reviews = ref(new Set<string>())
const material = ref<IntegrationMaterial[]>([]), materialNext = ref<string | null>(null), dataLocations = ref(''), updateBehavior = ref(''), rollbackBehavior = ref(''), recoveryPlan = ref(''), verification = ref(''), references = ref(''), materialReason = ref('')
const locked = computed(() => mutation.blocked.value), time = (value: string | null) => value ? formatTime(value, caps.value?.timeZone ?? 'UTC') : '—'
let timer: ReturnType<typeof setTimeout> | null = null, disposed = false
async function fail(e: unknown) { failure.value = e instanceof Error ? e.message : '读取失败。'; if (e instanceof ApiError && [401,403,404].includes(e.status)) await session.load() }
function query() { const q = new URLSearchParams({ softwareId, pageSize: '50' }); for (const [k,v] of Object.entries(filter.value)) if (v) q.set(k,v); return q }
async function loadInstances(cursor?: string | null) { const q = query(); if (cursor) q.set('cursor',cursor); const p = await request<Page<Instance>>('/api/v1/manage/instances?' + q); instances.value = cursor ? [...instances.value,...p.items] : p.items; instanceNext.value = p.nextCursor }
async function loadDeployments(cursor?: string | null) { const q = new URLSearchParams({ softwareId, pageSize: '50' }); if (cursor) q.set('cursor',cursor); const p = await request<Page<Deployment>>('/api/v1/manage/deployments?' + q); deployments.value = cursor ? [...deployments.value,...p.items] : p.items; deploymentNext.value = p.nextCursor }
async function loadReleases(cursor?: string | null) { const q = new URLSearchParams({ channel: 'Formal', pageSize: '50' }); if (cursor) q.set('cursor',cursor); const p = await request<Page<Release>>(`/api/v1/manage/software/${softwareId}/releases?${q}`); releases.value = cursor ? [...releases.value,...p.items] : p.items; releaseNext.value = p.nextCursor }
async function load() {
  loading.value = true; failure.value = ''
  try { if (canCreate.value) caps.value = await request<DeploymentCapabilities>(`/api/v1/manage/software/${softwareId}/deployment-capabilities`)
    if (canRead.value) await Promise.all([loadInstances(),loadDeployments()]); if (session.can('software.read',softwareId)) await loadReleases()
  } catch(e) { await fail(e) } finally { loading.value = false }
}
async function detail(id: string) {
  try { const d = await request<Deployment>('/api/v1/manage/deployments/' + id); selected.value = d
    const [b,t,a] = await Promise.all([request<Page<Batch>>(`/api/v1/manage/deployments/${id}/batches`),request<Page<InstanceTask>>(`/api/v1/manage/deployments/${id}/tasks`),request<Page<Admission>>(`/api/v1/manage/deployments/${id}/targets`)])
    batches.value = b.items; batchNext.value = b.nextCursor; tasks.value = t.items; taskNext.value = t.nextCursor; admissions.value = a.items; admissionNext.value = a.nextCursor
  } catch(e) { selected.value = null; tasks.value = []; batches.value = []; admissions.value = []; await fail(e) }
}
async function more(kind: 'tasks' | 'targets' | 'batches') { if (!selected.value) return; const cursor = kind === 'tasks' ? taskNext.value : kind === 'batches' ? batchNext.value : admissionNext.value; if (!cursor) return
  try { if (kind === 'tasks') { const p = await request<Page<InstanceTask>>(`/api/v1/manage/deployments/${selected.value.id}/tasks?cursor=${encodeURIComponent(cursor)}`); tasks.value.push(...p.items); taskNext.value = p.nextCursor }
    else if(kind === 'batches') { const p=await request<Page<Batch>>(`/api/v1/manage/deployments/${selected.value.id}/batches?cursor=${encodeURIComponent(cursor)}`); batches.value.push(...p.items); batchNext.value=p.nextCursor }
    else { const p = await request<Page<Admission>>(`/api/v1/manage/deployments/${selected.value.id}/targets?cursor=${encodeURIComponent(cursor)}`); admissions.value.push(...p.items); admissionNext.value = p.nextCursor } } catch(e) { await fail(e) }
}
function toggle(id: string) { const set = new Set(chosen.value); if (set.has(id)) set.delete(id); else set.add(id); chosen.value = set }
const selectedNames = computed(() => instances.value.filter(x => chosen.value.has(x.id)).map(x => `${x.deviceName} / ${x.deviceNo} / ${x.id}`))
function filterInput() { return Object.fromEntries([['softwareId',softwareId],...Object.entries(filter.value).map(([k,v]) => [k,v || null])]) }
async function launch() {
  if (!canCreate.value || !caps.value || busy.value) return
  if (!launchActive.value) {
    if (!reason.value.trim() || mode.value === 'Explicit' && chosen.value.size === 0) { failure.value = '请选择目标实例并填写投放原因。'; return }
    if (!useDefaultWindow.value && (!utcInput(start.value) || !utcInput(end.value) || utcInput(start.value)! >= utcInput(end.value)!)) { failure.value = '请填写有效的 UTC 开始与最晚开始时间。'; return }
    launchActive.value = true; launchIds.value = [...chosen.value].sort(); launchStage.value = 'selection'; chunk.value = 0; selection.value = null
  }
  pendingAction.value = 'launch'; failure.value = ''
  if (launchStage.value === 'selection') {
    const s = await mutation.perform<Selection>('/api/v1/manage/target-selections','POST',{ softwareId,mode:mode.value,filter:mode.value === 'Filter' ? filterInput() : null },session.current!.csrfToken)
    if (!s || !mutation.completed.value) return; selection.value = s; launchStage.value = mode.value === 'Explicit' ? 'chunks' : 'seal'
  }
  if (mode.value === 'Filter' && selection.value?.state !== 'Sealed') {
    try { selection.value = await request<Selection>('/api/v1/manage/target-selections/' + selection.value!.id) } catch(e) { await fail(e); return }
    if (selection.value.state === 'Failed') { launchActive.value = false; failure.value = '筛选快照失败，请核对条件后重新创建。'; return }
    if (selection.value.state !== 'Sealed') { notice.value = '后台正在封存筛选快照，请刷新封存结果后继续。'; return }
  }
  if (launchStage.value === 'chunks') {
    const size = caps.value.selectionChunkSize
    while (chunk.value * size < launchIds.value.length) {
      const s = await mutation.perform<Selection>(`/api/v1/manage/target-selections/${selection.value!.id}/chunks/${chunk.value}`,'PUT',{ instanceIds:launchIds.value.slice(chunk.value*size,(chunk.value+1)*size) },session.current!.csrfToken)
      if (!s || !mutation.completed.value) return; selection.value = s; chunk.value++
    }
    launchStage.value = 'seal'
  }
  if (launchStage.value === 'seal' && mode.value === 'Explicit') {
    const s = await mutation.perform<Selection>(`/api/v1/manage/target-selections/${selection.value!.id}/seal`,'POST',{ expectedRevision:selection.value!.revision,chunkCount:chunk.value,expectedDistinctCount:launchIds.value.length },session.current!.csrfToken)
    if (!s || !mutation.completed.value) return; selection.value = s
  }
  launchStage.value = 'deployment'
  if (!selection.value?.memberCount) { failure.value = '封存集合为空，不能创建投放。'; launchActive.value = false; return }
  const d = await mutation.perform<Deployment>('/api/v1/manage/deployments','POST',{ softwareId,selectionId:selection.value.id,kind:'Update',targetReleaseId:selectedReleaseId.value || null,window:useDefaultWindow.value ? null : { notBefore:utcInput(start.value),latestStart:utcInput(end.value) },reason:reason.value,retryOfDeploymentId:retryOf.value },session.current!.csrfToken)
  if (!d || !mutation.completed.value) { await session.load(); return }
  launchActive.value = false; launchOpen.value = false; chosen.value = new Set(); retryOf.value = null; notice.value = '投放已登记，后台将逐台准入；拒绝对象不会计为安装失败。'
  if (canRead.value) { await loadDeployments(); await detail(d.id) }
}
async function control(action: string, task?: InstanceTask) {
  if (!selected.value || !controlReason.value.trim() || locked.value) return
  const body: Record<string,unknown> = { expectedRevision:task?.revision ?? selected.value.revision,reason:controlReason.value }
  if (action === 'resume') body.reviewedFailures = batches.value.filter(x => reviews.value.has(x.id)).map(x => ({ batchId:x.id,failureRevision:x.failureRevision }))
  if (action === 'reschedule') { const a = utcInput(controlStart.value), b = utcInput(controlEnd.value); if (!a || !b || a >= b) { failure.value = '请填写有效的 UTC 新时段。'; return } body.window = { notBefore:a,latestStart:b } }
  if (action === 'close-unknown') { if (!checkedOnsite.value || !onsite.value.trim()) { failure.value = '请填写现场核实材料并确认没有正在安装。'; return } body.onsiteEvidence = onsite.value; body.noActiveInstallationConfirmed = true }
  pendingAction.value = 'control'
  const result = await mutation.perform<Deployment | TaskWork | InstanceTask>(task ? `/api/v1/manage/tasks/${task.id}/${action}` : `/api/v1/manage/deployments/${selected.value.id}/${action}`,'POST',body,session.current!.csrfToken)
  if (result && mutation.completed.value) {
    if ('stage' in result) { controlWork.value = result; if (canRead.value) await showControlWork(result.id) }
    notice.value = action === 'pause' ? '已暂停后续许可；现场进程继续由接入方负责。' : '操作已登记。'
    if (canRead.value) { await detail(selected.value.id); await loadDeployments() }
  } else await session.load()
}
async function verify() {
  if (pendingAction.value === 'launch') { await launch(); return }
  const p = mutation.pending.value; if (!p) return
  const result = await mutation.perform<Deployment | TaskWork | InstanceTask | IntegrationMaterial>(p.path,p.method,p.body,session.current!.csrfToken)
  if (result && mutation.completed.value) { if ('stage' in result) { controlWork.value = result; if (canRead.value) await showControlWork(result.id) } if (selected.value && canRead.value) await detail(selected.value.id); if (pendingAction.value === 'material') await loadMaterials(); if (canRead.value) await loadDeployments(); notice.value = '已按原键和原正文核实操作。' }
}
async function showControlWork(id: string, cursor?: string | null) { try {
  controlWork.value=await request<TaskWork>('/api/v1/manage/deployment-work/'+id)
  const p=await request<Page<ControlItem>>(`/api/v1/manage/deployment-work/${id}/items${cursor ? '?cursor='+encodeURIComponent(cursor) : ''}`)
  controlItems.value=cursor ? [...controlItems.value,...p.items] : p.items; controlNext.value=p.nextCursor
} catch(e) { await fail(e) } }
function resetDraft() { if(locked.value) return; launchActive.value=false; selection.value=null; launchStage.value='selection'; chunk.value=0; notice.value='已解锁表单。先前未投放的目标集合仍保留，可重新核对后创建。' }
watch(canRead, value=> { if(!value) { selected.value=null; deployments.value=[]; tasks.value=[]; instances.value=[]; controlWork.value=null; receipts.value=[] } })
function retry() { if (!selected.value || !canCreate.value) return; const ended = tasks.value.filter(x => taskEnded(x.state)); if (!ended.length) { failure.value = '当前已加载任务中没有可重试的已结束对象。'; return }
  retryOf.value = selected.value.id; chosen.value = new Set(ended.map(x => x.instanceId)); mode.value = 'Explicit'; selectedReleaseId.value = selected.value.targetReleaseId; launchOpen.value = true; notice.value = '已选择当前加载的已结束任务对象。新投放保留原任务引用，仍会重新准入。' }
async function showReceipts(task: InstanceTask, cursor?: string | null) { try { const p = await request<Page<Receipt>>(`/api/v1/manage/tasks/${task.id}/receipts${cursor ? '?cursor='+encodeURIComponent(cursor) : ''}`); receipts.value = cursor ? [...receipts.value,...p.items] : p.items; receiptNext.value = p.nextCursor; receiptTask.value = task.id } catch(e) { await fail(e) } }
async function loadMaterials(cursor?: string | null) { if (!selectedReleaseId.value) return; try { const p = await request<Page<IntegrationMaterial>>(`/api/v1/manage/releases/${selectedReleaseId.value}/integration-materials${cursor ? '?cursor='+encodeURIComponent(cursor) : ''}`); material.value = cursor ? [...material.value,...p.items] : p.items; materialNext.value = p.nextCursor } catch(e) { await fail(e) } }
async function saveMaterial() { if (!selectedReleaseId.value || !materialReason.value.trim() || locked.value) return; pendingAction.value = 'material'
  const result = await mutation.perform(`/api/v1/manage/releases/${selectedReleaseId.value}/integration-materials`,'POST',{ dataLocations:lines(dataLocations.value),updateBehavior:updateBehavior.value,rollbackBehavior:rollbackBehavior.value,recoveryPlan:recoveryPlan.value,verificationConclusion:verification.value || null,evidenceReferences:lines(references.value),reason:materialReason.value },session.current!.csrfToken)
  if (result && mutation.completed.value) await loadMaterials(); else await session.load()
}
function poll() { if (disposed) return; timer = setTimeout(async () => { if (selected.value && !locked.value && ['Preparing','Running','Paused'].includes(selected.value.state)) await detail(selected.value.id); if(controlWork.value && !locked.value) await showControlWork(controlWork.value.id); poll() },(caps.value?.pollRetrySeconds ?? 10)*1000) }
onMounted(async () => { await load(); poll() }); onUnmounted(() => { disposed = true; if (timer) clearTimeout(timer) })
</script>
<template>
  <section class="page-heading"><div><p class="eyebrow">现场软件 / 正式版本</p><h1>更新投放</h1><p class="muted">只投放正式更新。实际安装、数据库和日志保护由接入方负责。</p></div><button :disabled="loading || locked" @click="load">刷新列表</button></section>
  <p v-if="failure || error" role="alert" class="error">{{ failure || error }}</p><p v-if="notice" role="status">{{ notice }}</p>
  <div v-if="uncertain" class="state-card"><p>提交结果未知。原键与原正文保留在此页，不会自动重新发送。</p><button :disabled="busy" @click="verify">核实原操作</button></div>
  <p v-if="loading" role="status">正在读取投放资料…</p>
  <p v-if="!canRead" class="state-card">查看实例、准入和进度需要该软件 instance.read 权限。</p>
  <section v-if="canCreate" class="panel"><div class="section-heading"><h2>目标投放</h2><button :disabled="locked || launchActive || !caps" @click="launchOpen = !launchOpen">选择更新目标</button></div>
    <p v-if="caps" class="muted">厂区时区 {{ caps.timeZone }} · 配置时段 {{ caps.defaultStartLocalTime }}—{{ caps.defaultLatestStartLocalTime }} · 每批 {{ caps.batchSize }} · 失败阈值 {{ caps.failureLimit }} · 等待 {{ caps.resultWaitSeconds }} 秒</p>
    <form v-if="launchOpen" @submit.prevent="launch"><fieldset :disabled="locked || launchActive"><label>正式目标版本<select v-model="selectedReleaseId"><option value="">创建时固定最新可用正式版</option><option v-for="r in releases.filter(x => x.state === 'Formal')" :key="r.id" :value="r.id">{{ r.version }} · {{ r.id }}</option></select></label><button v-if="releaseNext" type="button" @click="loadReleases(releaseNext)">更多正式版本</button>
      <label>目标方式<select v-model="mode"><option value="Explicit">明确选择实例</option><option value="Filter">按筛选条件封存</option></select></label>
      <div class="form-grid"><label>设备编号<input v-model="filter.deviceNo" /></label><label>工序标识<input v-model="filter.processId" placeholder="已有工序 ID，可留空" /></label><label>设备标识<input v-model="filter.deviceId" placeholder="已有设备 ID，可留空" /></label><label>上报 IP<input v-model="filter.reportedIp" /></label><label>已安装平台版本 ID<input v-model="filter.installedReleaseId" /></label><label>新鲜度<select v-model="filter.freshness"><option value="">全部</option><option value="Fresh">正常上报</option><option value="Unknown">状态未知</option><option value="NeverReported">尚未上报</option></select></label><label>运行状态<select v-model="filter.runningState"><option value="">全部</option><option value="Running">运行</option><option value="Stopped">停止</option><option value="Unknown">未知</option></select></label><label>接入状态<select v-model="filter.lifecycle"><option value="">全部</option><option value="Active">启用</option><option value="Suspended">暂停接入</option></select></label></div>
      <button v-if="canRead" type="button" @click="loadInstances().catch(fail)">查询目标实例</button><p v-if="mode === 'Filter'">提交后由服务器在同一个数据库快照内封存符合条件的成员。下方列表用于查看，不能代替封存结果。</p>
      <div v-if="canRead" class="table-wrap"><table><thead><tr><th>选择</th><th>设备名称 / 编号</th><th>实例</th><th>实际上报版本</th><th>接入</th></tr></thead><tbody><tr v-for="i in instances" :key="i.id"><td><input type="checkbox" :disabled="mode !== 'Explicit'" :checked="chosen.has(i.id)" @change="toggle(i.id)" :aria-label="'选择实例 '+i.id" /></td><td>{{ i.deviceName }} / {{ i.deviceNo }}</td><td>{{ i.id }}</td><td>{{ i.lastSnapshot?.installedVersion ?? '尚未上报' }}</td><td>{{ i.lifecycle }}</td></tr></tbody></table><p v-if="!instances.length">当前筛选没有可展示的实例。</p><button v-if="instanceNext" type="button" @click="loadInstances(instanceNext).catch(fail)">加载更多实例</button></div>
      <p v-if="mode === 'Explicit'">已选择 {{ chosen.size }} 个实例</p><ul><li v-for="name in selectedNames" :key="name">{{ name }}</li></ul>
      <label><input type="checkbox" v-model="useDefaultWindow" />使用配置的下一时段</label><div v-if="!useDefaultWindow" class="form-grid"><label>UTC 开始<input type="datetime-local" v-model="start" required /></label><label>UTC 最晚开始<input type="datetime-local" v-model="end" required /></label></div>
      <label>投放原因<textarea v-model="reason" maxlength="256" required /></label><p v-if="retryOf">重试来源：{{ retryOf }}；新投放重新准入，不修改原任务。</p>
    </fieldset><button type="submit" :disabled="locked || !caps">{{ launchActive ? '刷新封存结果并继续' : '封存目标并创建更新投放' }}</button><button v-if="launchActive && !uncertain" type="button" :disabled="busy" @click="resetDraft">重新核对目标表单</button><p v-if="selection">封存状态 {{ selection.state }} · {{ selection.memberCount }} 个成员</p></form>
  </section>
  <section v-if="canRead" class="panel"><h2>投放记录</h2><div class="table-wrap"><table><thead><tr><th>投放</th><th>状态</th><th>准入</th><th>结果</th></tr></thead><tbody><tr v-for="d in deployments" :key="d.id"><td><button :disabled="locked" @click="detail(d.id)">{{ d.id }}</button></td><td>{{ taskState(d.state) }}</td><td>{{ d.processedCount }}/{{ d.selectedCount }} · 合格 {{ d.acceptedCount }} · 拒绝 {{ d.rejectedCount }}</td><td>成功 {{ d.resultCounts.succeeded }} / 失败 {{ d.resultCounts.failed }} / 未结束 {{ d.resultCounts.unfinished }}</td></tr></tbody></table></div><p v-if="!deployments.length">尚无可查看的投放。</p><button v-if="deploymentNext" :disabled="locked" @click="loadDeployments(deploymentNext).catch(fail)">更多投放</button></section>
  <section v-if="selected" class="panel"><div class="section-heading"><h2>投放详情 · {{ taskState(selected.state) }}</h2><button :disabled="locked" @click="detail(selected.id)">刷新当前投放</button></div><p>固定目标 {{ selected.targetReleaseId }} · 时段 {{ time(selected.window.notBefore) }}—{{ time(selected.window.latestStart) }}</p><p v-if="selected.controlPending">控制工作处理中，禁止新开始许可及开放下一批。</p><p v-for="p in selected.pauseReasons" :key="p.code">暂停原因：{{ p.code }} · {{ p.scope === 'FutureBatches' ? '后续批次' : '未取得许可的任务' }}</p>
    <div class="table-wrap"><table><thead><tr><th>批次</th><th>状态</th><th>任务 / 暂缓</th><th>失败修订</th><th>超时</th><th>复核</th></tr></thead><tbody><tr v-for="b in batches" :key="b.id"><td>{{ b.ordinal }}</td><td>{{ taskState(b.state) }}</td><td>{{ b.taskCount }} / {{ b.deferredCount }}</td><td>{{ b.failedCount }} · {{ b.failureRevision }} / 已复核 {{ b.reviewedFailureRevision }}</td><td>{{ b.timedOutCount }}</td><td><input v-if="canControl && b.failureRevision > b.reviewedFailureRevision" type="checkbox" :value="b.id" v-model="reviews" :disabled="locked" :aria-label="'复核批次 '+b.ordinal" /></td></tr></tbody></table></div>
    <button v-if="batchNext" :disabled="locked" @click="more('batches')">更多批次</button>
    <fieldset v-if="canControl" :disabled="locked"><legend>投放与逐台控制</legend><label>控制原因<input v-model="controlReason" maxlength="256" /></label><div class="actions"><button :disabled="selected.controlPending" @click="control('pause')">暂停投放</button><button :disabled="!canCreate" @click="control('resume')">复核并继续</button><button :disabled="selected.controlPending" @click="control('cancel')">取消未获许可任务</button><button :disabled="!canCreate" @click="retry">重试已结束任务</button></div><div class="form-grid"><label>UTC 新开始<input type="datetime-local" v-model="controlStart" /></label><label>UTC 新最晚开始<input type="datetime-local" v-model="controlEnd" /></label></div><button :disabled="selected.controlPending" @click="control('reschedule')">登记改期</button><p class="muted">暂停和取消不会停止现场安装。已授权任务的窗口与结果等待不随改期延长。</p><label>现场核实材料<textarea v-model="onsite" maxlength="2000" /></label><label><input type="checkbox" v-model="checkedOnsite" />已现场核实没有正在安装</label></fieldset>
    <article v-if="controlWork"><h3>控制工作 · {{ taskState(controlWork.state) }}</h3><p>{{ controlWork.id }} · {{ controlWork.stage }} · 已处理 {{ controlWork.processedItems }}</p><p v-if="controlWork.lastErrorCode" role="alert">{{ controlWork.lastErrorCode }}；修正条件后复核并继续原工作。</p><p v-for="item in controlItems" :key="item.taskId">{{ item.taskId }} · {{ item.outcome }} · {{ item.reasonCode }}</p><button v-if="controlNext" :disabled="locked" @click="showControlWork(controlWork.id,controlNext)">更多控制结果</button></article>
    <h3>逐台任务</h3><div class="table-wrap"><table><thead><tr><th>实例 / 任务</th><th>状态 / 进度</th><th>等待截止</th><th>结果</th><th>操作</th></tr></thead><tbody><tr v-for="t in tasks" :key="t.id"><td>{{ t.instanceId }}<small>{{ t.id }}</small></td><td>{{ taskState(t.state) }} {{ t.isDeferred ? '· 暂缓' : '' }}<small>{{ t.lastReportedProgress }}</small><small>{{ waitReason(t.waitReason) }}</small></td><td>{{ time(t.responseDeadlineAt) }}</td><td>{{ t.terminalResult ?? '尚无终态' }}<small v-if="t.manualClosure">现场关闭：{{ t.manualClosure.onsiteEvidence }}</small></td><td><button :disabled="locked" @click="showReceipts(t)">回执</button><template v-if="canControl && !taskEnded(t.state)"><button :disabled="locked" @click="control(t.isDeferred ? 'restore' : 'defer',t)">{{ t.isDeferred ? '恢复任务' : '暂缓' }}</button><button v-if="!t.startAuthorizedAt" :disabled="locked" @click="control('cancel',t)">取消任务</button><button v-if="t.state === 'AwaitingResult' && session.can('task.closeUnknown',softwareId)" :disabled="locked" @click="control('close-unknown',t)">人工关闭未知</button></template></td></tr></tbody></table></div><button v-if="taskNext" :disabled="locked" @click="more('tasks')">更多任务</button>
    <h3>逐台准入</h3><p v-if="!admissions.length">尚未形成准入结果。</p><p v-for="a in admissions" :key="a.instanceId">{{ a.instanceId }} · {{ a.decision === 'Accepted' ? '合格' : admissionReason(a.reasonCode) }}</p><button v-if="admissionNext" :disabled="locked" @click="more('targets')">更多准入结果</button>
    <div v-if="receiptTask"><h3>任务回执 · {{ receiptTask }}</h3><p v-if="!receipts.length">尚无回执。</p><p v-for="r in receipts" :key="r.id">序号 {{ r.sequence }} · {{ r.progress ?? r.result }} · {{ r.applied ? '已应用' : '仅保留事实' }} {{ r.lateAfterClosure ? '· 人工关闭后的迟到回执' : '' }} · {{ r.detail }}</p><button v-if="receiptNext" @click="showReceipts(tasks.find(t => t.id === receiptTask)!,receiptNext)">更多回执</button></div>
  </section>
  <section v-if="session.can('software.read',softwareId)" class="panel"><h2>版本数据保护资料</h2><label>版本<select v-model="selectedReleaseId" :disabled="locked"><option value="">选择正式版本</option><option v-for="r in releases" :key="r.id" :value="r.id">{{ r.version }}</option></select></label><button :disabled="locked || !selectedReleaseId" @click="loadMaterials()">查询资料历史</button><p>资料按修订保留；不代表结构化兼容结论、自动保护或现场验收。</p><article v-for="m in material" :key="m.id"><h3>修订 {{ m.revision }} · {{ time(m.recordedAt) }}</h3><p>位置：{{ m.dataLocations.join('；') }}</p><p>升级：{{ m.updateBehavior }}</p><p>回退：{{ m.rollbackBehavior }}</p><p>恢复：{{ m.recoveryPlan }}</p><p>核验：{{ m.verificationConclusion ?? '未填写' }}</p><p>材料：{{ m.evidenceReferences.join('；') }}</p></article><button v-if="materialNext" @click="loadMaterials(materialNext)">更多资料修订</button>
    <form v-if="session.can('release.upload',softwareId)" @submit.prevent="saveMaterial"><fieldset :disabled="locked"><label>数据位置（每行一项）<textarea v-model="dataLocations" required /></label><label>升级行为<textarea v-model="updateBehavior" required /></label><label>回退行为<textarea v-model="rollbackBehavior" required /></label><label>恢复说明<textarea v-model="recoveryPlan" required /></label><label>核验结论<textarea v-model="verification" /></label><label>证据引用（每行一项）<textarea v-model="references" /></label><label>登记原因<input v-model="materialReason" maxlength="256" required /></label><button :disabled="!selectedReleaseId" type="submit">登记新资料修订</button></fieldset></form>
  </section>
</template>
