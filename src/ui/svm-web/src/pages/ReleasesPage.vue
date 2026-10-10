<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, onBeforeRouteLeave } from 'vue-router'
import { ApiError, request } from '../api'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
import { categoryName, type Page, type Software } from '../catalog'
import { releaseState, packageStage, uploadPackage, type Release, type Package, type ReleaseUpload, type TestEvidence, type DownloadAudit } from '../packages'
import { pendingWriteKey } from '../mutation'
import { hashFile } from '../packages/hashFile'

const route = useRoute(), session = useSession(), mutation = useProtectedMutation()
const { busy, uncertain, error } = mutation
const globalPending = inject(pendingWriteKey, ref(false)), pendingAction = ref<'retry' | 'disable' | 'publish' | null>(null)
const softwareId = typeof route.query.softwareId === 'string' ? route.query.softwareId : ''
const software = ref<Software | null>(null), channel = ref(route.query.channel === 'Formal' ? 'Formal' : 'Test'), items = ref<Release[]>([]), selected = ref<Release | null>(null)
const evidenceNext = ref<string | null>(null), auditNext = ref<string | null>(null)
const pkg = ref<Package | null>(null), evidence = ref<TestEvidence[]>([]), audits = ref<DownloadAudit[]>([])
const cursors = ref(['']), nextCursor = ref<string | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const creating = ref(false), level = ref('Patch'), summary = ref(''), reason = ref(''), expectedVersion = ref('')
const file = ref<File | null>(null), digest = ref(''), hashedBytes = ref(0), hashing = ref(false), uploading = ref(false), uploadUnknown = ref(false)
const registered = ref<ReleaseUpload | null>(null), maxBytes = ref<number | null>(null), actionReason = ref('')
const chosenEvidence = ref(''), publishReason = ref(''), publishConclusion = ref('')
const canPublish = computed(() => session.can('release.publish', softwareId))
const publishReady = computed(() => selected.value?.state === 'Test' && pkg.value?.state === 'Ready' && pkg.value.healthyReplicaCount === 2 && pkg.value.sizeBytes === pkg.value.expectedSize && pkg.value.sha256 === pkg.value.expectedSha256)
const locked = computed(() => mutation.blocked.value || uploading.value || uploadUnknown.value)
const canUpload = computed(() => session.can('release.upload', softwareId)), canDisable = computed(() => session.can('release.disable', softwareId))
let hashAbort: AbortController | null = null, uploadAbort: AbortController | null = null, poll: ReturnType<typeof setTimeout> | null = null
let disposed = false
async function failed(e: unknown) {
  failure.value = e instanceof Error ? e.message : '版本读取失败。'
  if (e instanceof ApiError && [401, 403, 404].includes(e.status)) await session.load()
}
async function load(reset = false) {
  if (reset) cursors.value = ['']
  loading.value = true; failure.value = ''
  try {
    const query = new URLSearchParams({ channel: channel.value, pageSize: '50' })
    const cursor = cursors.value[cursors.value.length - 1]; if (cursor) query.set('cursor', cursor)
    const [result, info] = await Promise.all([request<Page<Release>>(`/api/v1/manage/software/${softwareId}/releases?${query}`), request<Software>('/api/v1/manage/software/' + softwareId)])
    if (!disposed) { items.value = result.items; nextCursor.value = result.nextCursor; software.value = info }
  } catch (e) { items.value = []; nextCursor.value = null; await failed(e) }
  finally { loading.value = false }
}
async function detail(id: string) {
  failure.value = ''
  try {
    const current = await request<Release>('/api/v1/manage/releases/' + id)
    if (current.softwareId !== softwareId) throw new Error('版本不属于当前软件。')
    if (selected.value?.id !== id) { chosenEvidence.value = ''; publishReason.value = ''; publishConclusion.value = '' }
    selected.value = current
    items.value = items.value.map(item => item.id === current.id ? current : item)
    pkg.value = await request<Package>('/api/v1/manage/packages/' + selected.value.packageId)
    const proof = await request<Page<TestEvidence>>(`/api/v1/manage/releases/${id}/test-evidence?pageSize=50`)
    evidence.value = proof.items; evidenceNext.value = proof.nextCursor
    if (session.can('audit.read', softwareId)) await moreAudit(true)
  } catch (e) { pkg.value = null; evidence.value = []; audits.value = []; await failed(e) }
}
async function moreEvidence() {
  if (!selected.value || !evidenceNext.value) return
  try { const page = await request<Page<TestEvidence>>(`/api/v1/manage/releases/${selected.value.id}/test-evidence?pageSize=50&cursor=${encodeURIComponent(evidenceNext.value)}`); evidence.value.push(...page.items); evidenceNext.value = page.nextCursor } catch (e) { await failed(e) }
}
async function moreAudit(reset = false) {
  if (!pkg.value) return
  try { const query = new URLSearchParams({ softwareId, pageSize: '50' }); if (!reset && auditNext.value) query.set('cursor', auditNext.value)
    const page = await request<Page<DownloadAudit>>('/api/v1/manage/audit-events?' + query); const rows = page.items.filter(x => x.packageId === pkg.value!.id)
    audits.value = reset ? rows : [...audits.value, ...rows]; auditNext.value = page.nextCursor
  } catch (e) { audits.value = []; auditNext.value = null; await failed(e) }
}
async function choose(event: Event) {
  hashAbort?.abort(); digest.value = ''; hashedBytes.value = 0
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  if (!file.value) return
  if (!maxBytes.value || file.value.size < 1 || file.value.size > maxBytes.value) { failure.value = '安装包为空或超过本厂配置的包大小限制。'; file.value = null; return }
  const controller = new AbortController(); hashAbort = controller; hashing.value = true; failure.value = ''
  try { const value = await hashFile(file.value, count => { hashedBytes.value = count }, controller.signal); if (hashAbort === controller) {
    if (registered.value && pkg.value && (value !== pkg.value.expectedSha256 || file.value?.size !== pkg.value.expectedSize)) { failure.value = '重传必须使用原版本登记的大小与摘要，请选择原安装包。'; file.value = null }
    else digest.value = value
  } }
  catch (e) { if (!(e instanceof DOMException && e.name === 'AbortError')) failure.value = '摘要计算失败，请重新选择文件。' }
  finally { if (hashAbort === controller) hashing.value = false }
}
async function createAndUpload() {
  if (!file.value || !digest.value || hashing.value || uploading.value) return
  if (!registered.value) {
    const result = await mutation.perform<ReleaseUpload>(`/api/v1/manage/software/${softwareId}/releases`, 'POST', {
      changeLevel: level.value, changeSummary: summary.value, changeReason: reason.value, expectedVersion: expectedVersion.value || null,
      package: { fileName: file.value.name, sizeBytes: file.value.size, sha256: digest.value },
    }, session.current!.csrfToken)
    if (!mutation.completed.value || !result) { await session.load(); return }
    registered.value = result; selected.value = result.release; await load(true)
  }
  uploading.value = true; uploadUnknown.value = false; failure.value = ''; uploadAbort = new AbortController()
  try {
    pkg.value = await uploadPackage(registered.value.uploadPath, file.value, session.current!.csrfToken, uploadAbort.signal)
    creating.value = false; notice.value = '服务器已核对接收内容，后台将形成两个副本。'; await detail(registered.value.release.id)
  } catch (e) { uploadUnknown.value = e instanceof ApiError && e.uncertain; await failed(e) }
  finally { uploading.value = false }
}
async function verifyUpload() {
  if (!registered.value) return
  try {
    pkg.value = await request<Package>('/api/v1/manage/uploads/' + registered.value.uploadId)
    if (pkg.value.sizeBytes !== null) { uploadUnknown.value = false; creating.value = false; notice.value = '已核实接收事实。'; await detail(registered.value.release.id) }
    else { failure.value = '服务器尚未确认完整接收。可等待再核实，或人工重新传输原安装包；保留当前上传标识。' }
  } catch (e) { await failed(e) }
}
async function retry() {
  if (!pkg.value || !actionReason.value || uncertain.value && pendingAction.value !== 'retry') return
  pendingAction.value = 'retry'
  const result = await mutation.perform<Package>(`/api/v1/manage/packages/${pkg.value.id}/retry`, 'POST', { reason: actionReason.value, expectedRevision: pkg.value.revision }, session.current!.csrfToken)
  if (mutation.completed.value && result) { pkg.value = result; actionReason.value = ''; notice.value = '原版本的副本工作已重新派发。' } else await session.load()
}
async function publish() {
  if (!selected.value || !session.current || uncertain.value && pendingAction.value !== 'publish') return
  pendingAction.value = 'publish'
  const result = await mutation.perform<Release>(`/api/v1/manage/releases/${selected.value.id}/publish`, 'POST', { testEvidenceId: chosenEvidence.value, publishReason: publishReason.value, publishConclusion: publishConclusion.value, expectedRevision: selected.value.revision }, session.current.csrfToken)
  if (mutation.completed.value && result) {
    selected.value = result; channel.value = 'Formal'; chosenEvidence.value = ''; publishReason.value = ''; publishConclusion.value = ''
    notice.value = `${result.version} 已转为正式版。`; await load(true); await detail(result.id)
  } else await session.load()
}
async function latestFormal() {
  if (locked.value || !software.value?.latestAvailableFormalReleaseId) return
  channel.value = 'Formal'; await load(true); if (software.value.latestAvailableFormalReleaseId) await detail(software.value.latestAvailableFormalReleaseId)
}
async function disable() {
  if (!selected.value || !actionReason.value || uncertain.value && pendingAction.value !== 'disable') return
  pendingAction.value = 'disable'
  const result = await mutation.perform<Release>(`/api/v1/manage/releases/${selected.value.id}/disable`, 'POST', { reason: actionReason.value, expectedRevision: selected.value.revision }, session.current!.csrfToken)
  if (mutation.completed.value && result) { selected.value = result; actionReason.value = ''; await detail(result.id); await load() } else await session.load()
}
function resume() {
  if (locked.value || !selected.value || !pkg.value) return
  registered.value = { release: selected.value, uploadId: pkg.value.uploadId, uploadPath: `/api/v1/manage/uploads/${pkg.value.uploadId}/content` }; creating.value = true; file.value = null; digest.value = ''; failure.value = ''
}
function begin() { if (locked.value) return; registered.value = null; creating.value = true; file.value = null; digest.value = ''; summary.value = ''; reason.value = ''; expectedVersion.value = ''; notice.value = '' }
function close() { if (!locked.value) { hashAbort?.abort(); creating.value = false; file.value = null } }
function cancelHash() { hashAbort?.abort() }
function cancelUpload() { uploadAbort?.abort() }
async function changeChannel() { if (!locked.value) { selected.value = null; pkg.value = null; await load(true) } }
async function next() { if (nextCursor.value) { cursors.value.push(nextCursor.value); await load() } }
async function previous() { cursors.value.pop(); await load() }
async function refresh() { if (selected.value && !mutation.uncertain.value) { await detail(selected.value.id); mutation.error.value = ''; actionReason.value = '' } }
async function tick() {
  if (disposed) return
  if (!locked.value && selected.value && pkg.value && ['AwaitingReceipt', 'Copying', 'Receiving'].includes(pkg.value.processingStage)) await detail(selected.value.id)
  if (!disposed) poll = setTimeout(tick, 5000)
}
onBeforeRouteLeave(() => { if (uploading.value || uploadUnknown.value) { failure.value = '请先等待或核实当前上传，再离开此页面。'; return false } hashAbort?.abort() })
const unload = (event: BeforeUnloadEvent) => { if (uploading.value || uploadUnknown.value) { event.preventDefault(); event.returnValue = '' } }
watch([mutation.blocked, uploading, uploadUnknown], ([write, stream, unknown]) => { globalPending.value = write || stream || unknown }, { flush: 'sync' })
watch(creating, value => { if (!value) hashAbort?.abort() })
onMounted(async () => {
  window.addEventListener('beforeunload', unload)
  try { maxBytes.value = (await request<{ maxPackageBytes: number }>('/api/v1/manage/capabilities')).maxPackageBytes; await load(); if (typeof route.query.releaseId === 'string') await detail(route.query.releaseId) }
  catch (e) { await failed(e) }
  poll = setTimeout(tick, 5000)
})
onUnmounted(() => { disposed = true; globalPending.value = false; hashAbort?.abort(); uploadAbort?.abort(); file.value = null; window.removeEventListener('beforeunload', unload); if (poll) clearTimeout(poll) })
</script>
<template>
  <RouterLink v-if="session.can('deployment.create',softwareId) || session.can('instance.read',softwareId)" class="text-link" :to="{ path: '/deployments', query: { softwareId, releaseId: selected?.state === 'Formal' ? selected.id : undefined } }">正式版本更新投放</RouterLink>
  <section class="workspace">
    <div class="page-heading"><div><div class="eyebrow">软件版本</div><h1>{{ software?.name ?? '版本与安装包' }}</h1><p v-if="software" class="muted">{{ software.code }} · {{ categoryName(software.category) }} · 同软件设备共用版本库</p></div><button v-if="canUpload" class="primary" :disabled="locked || !maxBytes" @click="begin">登记版本并上传</button></div>
    <p v-if="failure" class="error" role="alert">{{ failure }}</p><p v-if="notice" class="success" role="status">{{ notice }}</p>
    <div class="tab-row"><button :aria-pressed="channel === 'Test'" :disabled="locked" @click="channel = 'Test'; changeChannel()">测试版本</button><button :aria-pressed="channel === 'Formal'" :disabled="locked" @click="channel = 'Formal'; changeChannel()">正式版本</button></div>
    <div class="dialog-actions"><button :disabled="locked || loading" @click="load(true)">刷新列表</button><button v-if="software?.latestAvailableFormalReleaseId" :disabled="locked" @click="latestFormal">查看最新可用正式版</button></div>
    <div class="account-layout">
      <section class="panel"><div v-if="loading" class="empty" role="status">正在读取版本…</div><div v-else-if="!items.length" class="empty">{{ channel === 'Formal' ? '尚无正式版本。完成测试并转正式后将在此展示。' : '没有可查看的测试版本。' }}</div>
        <div v-else class="table-scroll"><table><thead><tr><th>版本</th><th>状态</th><th>更新内容</th><th>登记时间</th></tr></thead><tbody><tr v-for="item in items" :key="item.id" :class="{ selected: selected?.id === item.id }"><td><button class="text-button" :disabled="locked" @click="detail(item.id)">{{ item.version }}</button></td><td>{{ releaseState(item.state) }}</td><td>{{ item.changeSummary }}</td><td>{{ new Date(item.createdAt).toLocaleString() }}</td></tr></tbody></table></div>
        <footer class="pagination"><span>本页可查看 {{ items.length }} 项</span><div><button :disabled="loading || locked || cursors.length <= 1" @click="previous">上一页</button><button :disabled="loading || locked || !nextCursor" @click="next">下一页</button></div></footer>
      </section>
      <aside class="panel detail"><template v-if="selected"><h2>{{ selected.version }} <span class="badge">{{ releaseState(selected.state) }}</span></h2><p class="description">{{ selected.changeSummary }}</p><dl><div><dt>变更级别</dt><dd>{{ selected.changeLevel }}</dd></div><div><dt>变更原因</dt><dd>{{ selected.changeReason }}</dd></div><div><dt>修订</dt><dd>{{ selected.revision }}</dd></div><div v-if="selected.disabledAt"><dt>停用原因</dt><dd>{{ selected.disableReason }}</dd></div></dl>
        <template v-if="pkg"><h3>安装包</h3><dl><div><dt>处理阶段</dt><dd>{{ packageStage(pkg.processingStage) }}</dd></div><div><dt>已核对副本</dt><dd>{{ pkg.healthyReplicaCount }} / 2</dd></div><div><dt>大小</dt><dd>{{ pkg.sizeBytes ?? '尚未确认' }} 字节（预期 {{ pkg.expectedSize }}）</dd></div><div><dt>SHA-256</dt><dd class="digest">{{ pkg.sha256 ?? '尚未确认' }}</dd></div></dl><p v-if="pkg.lastErrorCode" class="error">{{ pkg.lastErrorCode }}</p><a v-if="pkg.downloadAvailable && pkg.downloadPath" class="primary text-link" :href="pkg.downloadPath" download>下载安装包</a><p v-else class="muted">当前不可下载。首次开放测试版须完成两个副本核对。</p>
          <button v-if="canUpload && selected.state === 'Staging' && ['AwaitingUpload', 'UploadFailed', 'Receiving'].includes(pkg.processingStage)" :disabled="locked" @click="resume">重传原版本安装包</button>
          <label v-if="canDisable && selected.state !== 'Disabled' || canUpload && pkg.processingStage === 'CopyFailed'">操作原因<input v-model="actionReason" maxlength="256" :disabled="locked" /></label>
          <div class="dialog-actions"><button v-if="canUpload && pkg.processingStage === 'CopyFailed'" :disabled="busy || !actionReason || uncertain && pendingAction !== 'retry'" @click="retry">{{ uncertain ? '核实原操作' : '重试副本工作' }}</button><button v-if="canDisable && selected.state !== 'Disabled'" :disabled="busy || !actionReason || uncertain && pendingAction !== 'disable'" @click="disable">{{ uncertain ? '核实原操作' : '停用版本' }}</button><button v-if="error && !uncertain" @click="refresh">加载最新详情</button></div><p v-if="error" class="error">{{ error }}</p><p v-if="uncertain" class="muted">结果待核实；原请求及操作键保留在当前页面内存中。</p>
        </template>
        <section v-if="selected.publishedAt" class="publication-history" aria-labelledby="publication-history-title"><h3 id="publication-history-title">正式发布记录</h3><dl><div><dt>发布工号</dt><dd>{{ selected.publishedEmployeeNo }}</dd></div><div><dt>发布时间</dt><dd>{{ new Date(selected.publishedAt).toLocaleString() }}</dd></div><div><dt>通过原因</dt><dd>{{ selected.publishReason }}</dd></div><div><dt>测试结论</dt><dd class="conclusion">{{ selected.publishConclusion }}</dd></div><div><dt>证据标识</dt><dd>{{ selected.testEvidenceId }}</dd></div></dl></section>
        <section v-if="selected.state === 'Test' && canPublish || uncertain && pendingAction === 'publish'" class="publication-panel" aria-labelledby="publication-title"><h3 id="publication-title">测试转正式</h3><p class="muted">选择此版本的真实安装证据并填写结论，工号和时间由平台记录。</p><p v-if="!publishReady" class="muted">发布需两个当前健康副本；请等待核对或修复后刷新详情。</p><p v-if="!evidence.length" class="muted">尚无可选择的安装证据，不能发布。</p><form @submit.prevent="publish"><fieldset :disabled="locked"><label>安装证据<select v-model="chosenEvidence" required><option value="" disabled>选择安装上报</option><option v-for="item in evidence" :key="item.id" :value="item.id">{{ item.installedVersion }} · {{ item.instanceId }} · {{ new Date(item.receivedAt).toLocaleString() }} · {{ item.reportedRunningState }}</option></select></label><label>测试通过原因<input v-model="publishReason" required maxlength="256" /></label><label>测试结论<textarea v-model="publishConclusion" required maxlength="2000" rows="3" /></label></fieldset><button class="primary" :disabled="busy || uncertain && pendingAction !== 'publish' || !uncertain && (!canPublish || !publishReady || !chosenEvidence || !publishReason.trim() || !publishConclusion.trim())">{{ uncertain ? '核实原发布操作' : '转为正式版' }}</button></form></section>
        <h3>测试安装证据</h3><p class="muted">下载完成不代表安装成功；证据来自实例引用此版本的有效上报。</p><ul v-if="evidence.length" class="permission-list"><li v-for="item in evidence" :key="item.id">{{ item.installedVersion }} · {{ item.instanceId }}<small>{{ new Date(item.receivedAt).toLocaleString() }} · {{ item.reportedRunningState }}</small></li></ul><p v-else class="muted">尚无关联安装证据。</p><button v-if="evidenceNext" @click="moreEvidence">更多安装证据</button>
        <template v-if="session.can('audit.read', softwareId)"><h3>下载传输记录（当前页）</h3><ul v-if="audits.length" class="permission-list"><li v-for="item in audits" :key="item.requestId">{{ item.employeeNo ?? item.actorKind }} · {{ item.state }}<small>{{ item.bytesSent ?? '尚未取得可信结束记录' }} 字节 · {{ item.startedAt }}</small></li></ul><p v-else class="muted">本页无此版本的下载记录。</p><button v-if="auditNext" @click="moreAudit()">更多下载记录</button></template>
      </template><div v-else class="empty">选择版本，查看更新详情、包处理和关联证据。</div></aside>
    </div>
    <section v-if="creating" class="panel upload-panel" aria-labelledby="upload-title"><h2 id="upload-title">{{ registered ? '上传原版本安装包' : '登记测试版本' }}</h2><p class="muted">首版为 1.0.0，后续按变更级别生成。上传失败保留编号，重传沿用当前版本。</p>
      <form @submit.prevent="createAndUpload"><fieldset :disabled="locked"><template v-if="!registered"><label v-if="!registered">变更级别<select v-model="level"><option>Patch</option><option>Minor</option><option>Major</option></select></label><label>更新内容<textarea v-model="summary" required maxlength="2000" rows="3" /></label><label>变更原因<textarea v-model="reason" required maxlength="256" rows="2" /></label><label>预期编号（可选，用于并发核对）<input v-model="expectedVersion" maxlength="32" /></label></template><label>安装包<input type="file" required @change="choose" /></label></fieldset>
        <p v-if="file" class="muted">{{ file.name }} · {{ file.size }} 字节</p><p v-if="hashing" role="status">正在计算 SHA-256：{{ hashedBytes }} / {{ file?.size }} 字节 <button type="button" @click="cancelHash">取消摘要计算</button></p><p v-if="digest" class="digest">SHA-256：{{ digest }}</p><p v-if="registered" class="muted">已登记 {{ registered.release.version }}，上传标识 {{ registered.uploadId }}</p><p v-if="uploading" role="status">正在上传并等待服务器核对… <button type="button" @click="cancelUpload">取消上传</button></p>
        <p v-if="error" class="error">{{ error }}</p><p v-if="uncertain || uploadUnknown" class="muted">结果待核实；不会自动换键、另建版本或重发上传。</p>
        <div class="dialog-actions"><button v-if="!locked" type="button" @click="close">取消</button><button v-if="uploadUnknown" type="button" @click="verifyUpload">核实接收结果</button><button class="primary" :disabled="busy || uploading || hashing || !digest || !canUpload">{{ uncertain ? '核实原登记操作' : uploadUnknown ? '人工重传原安装包' : registered ? '重传原安装包' : '登记并上传' }}</button></div>
      </form>
    </section>
  </section>
</template>
<style scoped>
.digest { overflow-wrap: anywhere; word-break: break-word; min-width: 0; font-family: monospace; font-size: .82rem; }
.detail dl > div { display: grid; grid-template-columns: 5.5rem minmax(0, 1fr); gap: .75rem; }
.detail dd { min-width: 0; overflow-wrap: anywhere; }
.tab-row { display: flex; gap: .5rem; margin: 1rem 0; }
.tab-row button[aria-pressed="true"] { background: #edf3ff; color: #255dcc; border-color: #acc4ef; }
.upload-panel { margin-top: 1.5rem; padding: 1.5rem; }
.upload-panel fieldset, .publication-panel fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: 1rem; }
.publication-panel { margin-top: 1.5rem; padding-top: 1rem; border-top: 1px solid #dde5ef; }
.publication-panel select { width: 100%; min-width: 0; }
.publication-panel button { margin-top: 1rem; }
.conclusion { white-space: pre-wrap; }
</style>
