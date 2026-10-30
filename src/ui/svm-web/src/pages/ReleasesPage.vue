<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, onBeforeRouteLeave } from 'vue-router'
import { ApiError, request } from '../api'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
import { categoryName, type Page, type Software } from '../catalog'
import { releaseState, packageStage, uploadPackage, type Release, type Package, type ReleaseUpload, type TestEvidence, type DownloadAudit } from '../packages'
import { pendingWriteKey } from '../mutation'
import { hashFile } from '../packages/hashFile'
import type { ReleaseSource, ReleaseSyncWork, ReleaseSourceOption, ReleaseSourceOptions } from '../packages'
import { createReadScope } from '../reading'
import PlcPublication from '../components/PlcPublication.vue'
import { plcDownload } from '../plc'
import OperationStatus from '../components/OperationStatus.vue'

const route = useRoute(), session = useSession(), mutation = useProtectedMutation()
const { busy, uncertain, error } = mutation
const globalPending = inject(pendingWriteKey, ref(false)), pendingAction = ref<'retry' | 'disable' | 'publish' | 'sync' | 'source' | null>(null)
const softwareId = typeof route.query.softwareId === 'string' ? route.query.softwareId : ''
const software = ref<Software | null>(null), channel = ref(route.query.channel === 'Formal' ? 'Formal' : 'Test'), items = ref<Release[]>([]), selected = ref<Release | null>(null)
const evidenceNext = ref<string | null>(null), auditNext = ref<string | null>(null)
const pkg = ref<Package | null>(null), evidence = ref<TestEvidence[]>([]), audits = ref<DownloadAudit[]>([])
const cursors = ref(['']), nextCursor = ref<string | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const creating = ref(false), level = ref('Patch'), summary = ref(''), reason = ref(''), expectedVersion = ref('')
const file = ref<File | null>(null), digest = ref(''), hashedBytes = ref(0), hashing = ref(false), uploading = ref(false), uploadUnknown = ref(false)
const registered = ref<ReleaseUpload | null>(null), maxBytes = ref<number | null>(null), actionReason = ref('')
const chosenEvidence = ref(''), publishReason = ref(''), publishConclusion = ref('')
const source = ref<ReleaseSource | null>(null), syncWork = ref<ReleaseSyncWork | null>(null), historicalTag = ref('')
const isPlc = computed(() => software.value?.category === 'PLC')
const canManageSource = computed(() => !isPlc.value && session.can('release.source.manage', softwareId))
const sourceDialog = ref<HTMLDialogElement | null>(null), sourceOptions = ref<ReleaseSourceOption[]>([])
const sourceSearch = ref(''), sourceAppliedSearch = ref(''), sourceNextPage = ref<number | null>(null), sourceOptionsLoading = ref(false)
const sourceDraft = ref<{ softwareId: string; softwareName: string; revision: number; stamp: string; selected: ReleaseSourceOption | null; enabled: boolean; reason: string; blocking: string[] } | null>(null)
const sourceStamp = computed(() => JSON.stringify([session.current?.subjectId, session.access]))
const sourceDraftValid = computed(() => canManageSource.value && sourceDraft.value?.stamp === sourceStamp.value)
let sourceReturnFocus: HTMLElement | null = null
const canSync = computed(() => source.value?.kind === 'GitLab' && source.value.enabled && session.can('release.upload', softwareId))
const canPublish = computed(() => (isPlc.value || source.value?.kind === 'Manual') && session.can('release.publish', softwareId))
const publishReady = computed(() => selected.value?.state === 'Test' && pkg.value?.state === 'Ready' && pkg.value.healthyReplicaCount === 2 && pkg.value.sizeBytes === pkg.value.expectedSize && pkg.value.sha256 === pkg.value.expectedSha256)
const locked = computed(() => globalPending.value || mutation.blocked.value || uploading.value || uploadUnknown.value)
const canUpload = computed(() => (isPlc.value || source.value?.kind === 'Manual') && session.can('release.upload', softwareId)), canDisable = computed(() => session.can('release.disable', softwareId))
const canRead = computed(() => session.can('software.read', softwareId)), canAudit = computed(() => session.can('audit.read', softwareId))
const detailLoading = ref(false), evidenceLoading = ref(false), auditLoading = ref(false), syncLoading = ref(false)
const listRead = createReadScope(), detailRead = createReadScope(), evidenceRead = createReadScope(), auditRead = createReadScope(), sourceRead = createReadScope(), syncRead = createReadScope(), initialRead = createReadScope(), uploadVerificationRead = createReadScope()
const sourceOptionsRead = createReadScope()
const readScopes = [sourceOptionsRead,listRead, detailRead, evidenceRead, auditRead, sourceRead, syncRead, initialRead, uploadVerificationRead]
type ReadTicket = ReturnType<typeof listRead.capture>
let selectedId: string | null = null, detailGeneration = 0
function startRead(scope: typeof listRead) { scope.invalidate(); return scope.capture() }
function current(ticket: ReadTicket) { return !disposed && canRead.value && ticket.current() }
function read<T>(path: string, ticket: ReadTicket) { return request<T>(path, 'GET', undefined, undefined, undefined, { signal: ticket.signal }) }
function mergeRows<T>(old: T[], incoming: T[], key: (row: T) => string) { const rows = new Map(old.map(row => [key(row), row])); for (const row of incoming) rows.set(key(row), row); return [...rows.values()] }
function clearDetail() {
  selectedId = null; detailGeneration++; detailRead.invalidate(); evidenceRead.invalidate(); auditRead.invalidate()
  selected.value = null; pkg.value = null; evidence.value = []; audits.value = []; evidenceNext.value = null; auditNext.value = null
  detailLoading.value = false; evidenceLoading.value = false; auditLoading.value = false
  chosenEvidence.value = ''; publishReason.value = ''; publishConclusion.value = ''; actionReason.value = ''
}
const syncStages: Record<string, string> = { Pending: '等待同步', Scanning: '扫描发布', Historical: '历史版本，需手动同步', Downloading: '拉取安装包', VerifyingSource: '重新核对来源', PackageProcessing: '校验及形成双副本', Completed: '已完成', Conflict: '来源冲突，需人工核查' }
async function loadSource() {
  if (!canRead.value || disposed) return false
  const ticket = startRead(sourceRead), previousWorkId = syncWork.value?.id
  try {
    if (!software.value) { const product = await read<Software>(`/api/v1/manage/software/${softwareId}`,ticket); if(!current(ticket))return false; software.value=product }
    if (isPlc.value) { source.value=null; return true }
    const info = await read<ReleaseSource>(`/api/v1/manage/software/${softwareId}/release-source`, ticket)
    const work = (!syncWork.value || syncWork.value.kind === 'Scan' && syncWork.value.stage === 'Completed') && info.latestWorkId
      ? await read<ReleaseSyncWork>(`/api/v1/manage/release-sync-work/${info.latestWorkId}`, ticket) : null
    if (!current(ticket)) return false
    source.value = info
    if (work && syncWork.value?.id === previousWorkId) {
      if (work.id !== previousWorkId) { syncRead.invalidate(); syncLoading.value = false }
      syncWork.value = work
    }
    return true
  } catch (e) { if (current(ticket)) await failed(e); return false } finally { ticket.finish() }
}
async function fetchSourceOptions(page: number, search: string) {
  if (disposed || !canManageSource.value || sourceOptionsLoading.value) return null
  const ticket = startRead(sourceOptionsRead), stamp = sourceStamp.value
  sourceOptionsLoading.value = true
  try {
    const result = await read<ReleaseSourceOptions>(`/api/v1/manage/software/${softwareId}/release-source-options?` + new URLSearchParams({ search, page: String(page), pageSize: '50' }), ticket)
    if (disposed || !ticket.current() || !canManageSource.value || stamp !== sourceStamp.value) return null
    sourceOptions.value = page === 1 ? result.items : mergeRows(sourceOptions.value, result.items, x => `${x.connectionId}/${x.projectId}/${x.packageName}`)
    sourceAppliedSearch.value = search; sourceNextPage.value = result.nextPage
    return result
  } catch (e) { if (!disposed && ticket.current() && stamp === sourceStamp.value) await failed(e); return null }
  finally { if (ticket.current()) sourceOptionsLoading.value = false; ticket.finish() }
}
async function openSource() {
  if (locked.value || !canManageSource.value) return
  sourceReturnFocus = document.activeElement as HTMLElement | null
  sourceSearch.value = ''; failure.value = ''; mutation.error.value = ''
  const result = await fetchSourceOptions(1, '')
  if (!result) return
  const info = result.current
  const known = result.items.find(x => x.connectionId === info.connectionId && x.projectId === info.projectId && x.packageName === info.packageName)
  sourceDraft.value = { softwareId, softwareName: software.value?.name ?? '当前软件', revision: info.revision ?? 0, stamp: sourceStamp.value,
    selected: known ?? (info.connectionId && info.projectId && info.packageName ? { connectionId: info.connectionId, connectionName: '当前连接', baseUri: info.baseUri ?? '', projectId: info.projectId, projectName: String(info.projectId), packageName: info.packageName } : null),
    enabled: info.kind === 'Manual' ? true : info.enabled, reason: '', blocking: info.blockingWorkIds ?? [] }
  await nextTick(); sourceDialog.value?.showModal()
}
function closeSource() {
  if (locked.value) return
  sourceOptionsRead.invalidate(); sourceOptionsLoading.value = false; sourceDialog.value?.close(); sourceDraft.value = null; sourceOptions.value = []
  sourceReturnFocus?.focus()
}
async function saveSource() {
  if (uncertain.value) { if (pendingAction.value === 'source') await verifyOriginal(); return }
  const draft = sourceDraft.value, selected = draft?.selected
  if (!draft || !selected || !sourceDraftValid.value || !draft.reason.trim() || !session.current || locked.value) return
  pendingAction.value = 'source'
  const result = await mutation.perform<ReleaseSource>(`/api/v1/manage/software/${draft.softwareId}/release-source`, 'PUT',
    { connectionId: selected.connectionId, projectId: selected.projectId, packageName: selected.packageName, enabled: draft.enabled, expectedRevision: draft.revision, reason: draft.reason }, session.current.csrfToken)
  if (mutation.completed.value && result) {
    if (sourceDraftValid.value) source.value = result
    notice.value = '来源设置已保存，后台将核验实际发布；保存设置不代表 GitLab 已连接或版本已导入。'
    closeSource(); if (canRead.value) await loadSource()
  } else if (mutation.errorCode.value === 'REVISION_CONFLICT') failure.value = '来源已被修改，请关闭表单后重新读取，核对新来源再提交。'
  else if (mutation.errorCode.value === 'INVALID_STATE') failure.value = '仍有同步、导入或首次包处理工作未结束；可先停用新增同步，待相关工作完成后再改绑。'
}
async function synchronize() {
  if (!session.current || uncertain.value && pendingAction.value !== 'sync') return
  pendingAction.value = 'sync'
  const result = await mutation.perform<ReleaseSyncWork>(`/api/v1/manage/software/${softwareId}/release-sync`, 'POST', { tag: historicalTag.value || null }, session.current.csrfToken)
  if (mutation.completed.value && result) { syncRead.invalidate(); syncLoading.value = false; if (canRead.value && !disposed) syncWork.value = result; notice.value = '同步工作已登记，导入成功后可手动创建设备更新投放。'; await loadSource() }
  else await session.load()
}
async function refreshSync() {
  if (!canRead.value || disposed || syncLoading.value) return
  const ticket = startRead(syncRead), workId = syncWork.value?.id
  syncLoading.value = true
  try {
    const work = workId ? await read<ReleaseSyncWork>(`/api/v1/manage/release-sync-work/${workId}`, ticket) : null
    if (!current(ticket) || syncWork.value?.id !== workId) return
    if (work) syncWork.value = work
    await loadSource()
    // Work polling never changes the selected release or the visible release page.
  } catch (e) { if (current(ticket) && syncWork.value?.id === workId) await failed(e) }
  finally { if (current(ticket)) syncLoading.value = false; ticket.finish() }
}
let hashAbort: AbortController | null = null, uploadAbort: AbortController | null = null, poll: ReturnType<typeof setTimeout> | null = null
let disposed = false
async function failed(e: unknown) {
  if (disposed || e instanceof ApiError && e.cancelled) return
  failure.value = e instanceof Error ? e.message + (e instanceof ApiError && e.traceId ? `（关联号 ${e.traceId}）` : '') : '版本读取失败。'
  if (e instanceof ApiError && [401, 403, 404].includes(e.status)) await session.load()
}
async function load(reset = false, targetCursors = reset ? [''] : [...cursors.value]) {
  if (!canRead.value || disposed) return
  const ticket = startRead(listRead), requestedChannel = channel.value
  loading.value = true; failure.value = ''
  try {
    const query = new URLSearchParams({ channel: requestedChannel, pageSize: '50' })
    const cursor = targetCursors[targetCursors.length - 1]; if (cursor) query.set('cursor', cursor)
    const [result, info] = await Promise.all([read<Page<Release>>(`/api/v1/manage/software/${softwareId}/releases?${query}`, ticket), read<Software>('/api/v1/manage/software/' + softwareId, ticket)])
    if (current(ticket) && channel.value === requestedChannel) { items.value = result.items; nextCursor.value = result.nextCursor; software.value = info; cursors.value = targetCursors }
  } catch (e) { if (current(ticket) && channel.value === requestedChannel) await failed(e) }
  finally { if (current(ticket)) loading.value = false; ticket.finish() }
}
async function detail(id: string, preservePages = false) {
  if (!canRead.value || disposed) return
  const preserve = preservePages && selectedId === id && selected.value?.id === id
  if (!preserve) { clearDetail(); selectedId = id }
  const generation = detailGeneration, ticket = startRead(detailRead), allowedAudit = canAudit.value
  const ownsDetail = () => current(ticket) && selectedId === id && detailGeneration === generation
  detailLoading.value = true
  failure.value = ''
  try {
    const release = await read<Release>('/api/v1/manage/releases/' + id, ticket)
    if (!ownsDetail()) return
    if (release.softwareId !== softwareId || release.id !== id) throw new Error('版本不属于当前软件。')
    const [packageInfo, proof, auditPage] = await Promise.all([
      read<Package>('/api/v1/manage/packages/' + release.packageId, ticket),
      preserve || isPlc.value ? null : read<Page<TestEvidence>>(`/api/v1/manage/releases/${id}/test-evidence?pageSize=50`, ticket),
      !preserve && allowedAudit ? read<Page<DownloadAudit>>('/api/v1/manage/audit-events?' + new URLSearchParams({ softwareId, pageSize: '50' }), ticket) : null,
    ])
    if (!ownsDetail()) return
    if (packageInfo.id !== release.packageId) throw new Error('安装包不属于当前版本。')
    selected.value = release; pkg.value = packageInfo
    items.value = items.value.map(item => item.id === release.id ? release : item)
    if (proof) { evidence.value = mergeRows([], proof.items, row => row.id); evidenceNext.value = proof.nextCursor }
    if (auditPage && canAudit.value) { audits.value = mergeRows([], auditPage.items.filter(row => row.packageId === release.packageId), row => row.requestId); auditNext.value = auditPage.nextCursor }
  } catch (e) { if (ownsDetail()) await failed(e) }
  finally { if (ownsDetail()) detailLoading.value = false; ticket.finish() }
}
async function moreEvidence() {
  if (!selected.value || !evidenceNext.value || evidenceLoading.value || !canRead.value) return
  const id = selected.value.id, generation = detailGeneration, cursor = evidenceNext.value, ticket = startRead(evidenceRead)
  const ownsPage = () => current(ticket) && selectedId === id && detailGeneration === generation
  evidenceLoading.value = true
  try { const page = await read<Page<TestEvidence>>(`/api/v1/manage/releases/${id}/test-evidence?pageSize=50&cursor=${encodeURIComponent(cursor)}`, ticket)
    if (ownsPage()) { evidence.value = mergeRows(evidence.value, page.items, row => row.id); evidenceNext.value = page.nextCursor }
  } catch (e) { if (ownsPage()) await failed(e) } finally { if (ownsPage()) evidenceLoading.value = false; ticket.finish() }
}
async function moreAudit() {
  if (!pkg.value || !auditNext.value || auditLoading.value || !canAudit.value) return
  const id = selectedId, packageId = pkg.value.id, generation = detailGeneration, cursor = auditNext.value, ticket = startRead(auditRead)
  const ownsPage = () => current(ticket) && canAudit.value && selectedId === id && pkg.value?.id === packageId && detailGeneration === generation
  auditLoading.value = true
  try { const query = new URLSearchParams({ softwareId, pageSize: '50', cursor })
    const page = await read<Page<DownloadAudit>>('/api/v1/manage/audit-events?' + query, ticket)
    if (ownsPage()) { audits.value = mergeRows(audits.value, page.items.filter(row => row.packageId === packageId), row => row.requestId); auditNext.value = page.nextCursor }
  } catch (e) { if (ownsPage()) await failed(e) } finally { if (ownsPage()) auditLoading.value = false; ticket.finish() }
}
async function verifyOriginal() {
  const operation = mutation.pending.value
  if (!operation || !session.current || busy.value) return
  const result = await mutation.perform<ReleaseSource>(operation.path, operation.method, operation.body, session.current.csrfToken)
  if (mutation.completed.value && pendingAction.value === 'source') { if (canManageSource.value && result) source.value = result; closeSource(); if (canRead.value) await loadSource() }
  if (mutation.completed.value) notice.value = '已按原操作编号和正文核实结果；详情仍需当前查看权限。'
  await session.load()
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
  const actor = session.current?.subjectId
  const ownsUpload = () => !disposed && canRead.value && session.current?.subjectId === actor
  if (!registered.value) {
    const result = await mutation.perform<ReleaseUpload>(`/api/v1/manage/software/${softwareId}/releases`, 'POST', {
      changeLevel: level.value, changeSummary: summary.value, changeReason: reason.value, expectedVersion: expectedVersion.value || null,
      package: { fileName: file.value.name, sizeBytes: file.value.size, sha256: digest.value },
    }, session.current!.csrfToken)
    if (!mutation.completed.value || !result) { await session.load(); return }
    if (!ownsUpload()) return
    registered.value = result; selected.value = result.release; await load(true)
  }
  if (!ownsUpload() || !file.value) return
  const upload = registered.value, content = file.value
  uploading.value = true; uploadUnknown.value = false; failure.value = ''; uploadAbort = new AbortController()
  try {
    const result = await uploadPackage(upload.uploadPath, content, session.current!.csrfToken, uploadAbort.signal)
    if (!ownsUpload()) return
    pkg.value = result; creating.value = false; notice.value = '服务器已核对接收内容，后台将形成两个副本。'; await detail(upload.release.id)
  } catch (e) { if (!disposed && session.current?.subjectId === actor) { uploadUnknown.value = e instanceof ApiError && e.uncertain; await failed(e) } }
  finally { uploading.value = false }
}
async function verifyUpload() {
  if (!registered.value || !canRead.value || disposed) return
  const upload = registered.value, actor = session.current?.subjectId, ticket = startRead(uploadVerificationRead)
  const ownsUpload = () => current(ticket) && registered.value?.uploadId === upload.uploadId && session.current?.subjectId === actor
  try {
    const result = await read<Package>('/api/v1/manage/uploads/' + upload.uploadId, ticket)
    if (!ownsUpload()) return
    pkg.value = result
    if (result.sizeBytes !== null) { uploadUnknown.value = false; creating.value = false; notice.value = '已核实接收事实。'; await detail(upload.release.id) }
    else { failure.value = '服务器尚未确认完整接收。可等待再核实，或人工重新传输原安装包；保留当前上传标识。' }
  } catch (e) { if (ownsUpload()) await failed(e) } finally { ticket.finish() }
}
async function retry() {
  if (!pkg.value || !actionReason.value || uncertain.value && pendingAction.value !== 'retry') return
  pendingAction.value = 'retry'
  const result = await mutation.perform<Package>(`/api/v1/manage/packages/${pkg.value.id}/retry`, 'POST', { reason: actionReason.value, expectedRevision: pkg.value.revision }, session.current!.csrfToken)
  if (mutation.completed.value && result) { if (canRead.value && !disposed) pkg.value = result; actionReason.value = ''; notice.value = '原版本的副本工作已重新派发。' } else await session.load()
}
async function publish() {
  if (!selected.value || !session.current || uncertain.value && pendingAction.value !== 'publish') return
  pendingAction.value = 'publish'
  const result = await mutation.perform<Release>(`/api/v1/manage/releases/${selected.value.id}/publish`, 'POST', { testEvidenceId: chosenEvidence.value, publishReason: publishReason.value, publishConclusion: publishConclusion.value, expectedRevision: selected.value.revision }, session.current.csrfToken)
  if (mutation.completed.value && result) {
    if (canRead.value && !disposed) selected.value = result
    channel.value = 'Formal'; chosenEvidence.value = ''; publishReason.value = ''; publishConclusion.value = ''
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
  if (mutation.completed.value && result) { if (canRead.value && !disposed) selected.value = result; actionReason.value = ''; await detail(result.id); await load() } else await session.load()
}
function resume() {
  if (locked.value || !selected.value || !pkg.value) return
  registered.value = { release: selected.value, uploadId: pkg.value.uploadId, uploadPath: `/api/v1/manage/uploads/${pkg.value.uploadId}/content` }; creating.value = true; file.value = null; digest.value = ''; failure.value = ''
}
function begin() { if (locked.value) return; registered.value = null; creating.value = true; file.value = null; digest.value = ''; summary.value = ''; reason.value = ''; expectedVersion.value = ''; notice.value = '' }
function close() { if (!locked.value) { hashAbort?.abort(); creating.value = false; file.value = null } }
function cancelHash() { hashAbort?.abort() }
function cancelUpload() { uploadAbort?.abort() }
async function changeChannel() { if (!locked.value) { clearDetail(); items.value = []; nextCursor.value = null; await load(true) } }
async function next() { if (!loading.value && nextCursor.value) await load(false, [...cursors.value, nextCursor.value]) }
async function previous() { if (!loading.value && cursors.value.length > 1) await load(false, cursors.value.slice(0, -1)) }
async function refresh() { if (selected.value && !mutation.uncertain.value) { await detail(selected.value.id); mutation.error.value = ''; actionReason.value = '' } }
async function tick() {
  if (disposed) return
  if (!locked.value && source.value?.kind === 'GitLab' && syncWork.value && !['Completed', 'Conflict'].includes(syncWork.value.stage)) await refreshSync()
  if (!locked.value && selected.value && pkg.value && ['AwaitingReceipt', 'Copying', 'Receiving'].includes(pkg.value.processingStage)) await detail(selected.value.id, true)
  if (!disposed) poll = setTimeout(tick, 5000)
}
onBeforeRouteLeave(() => { if (uploading.value || uploadUnknown.value) { failure.value = '请先等待或核实当前上传，再离开此页面。'; return false } hashAbort?.abort() })
const unload = (event: BeforeUnloadEvent) => { if (uploading.value || uploadUnknown.value) { event.preventDefault(); event.returnValue = '' } }
watch([mutation.blocked, uploading, uploadUnknown], ([write, stream, unknown]) => { globalPending.value = write || stream || unknown }, { flush: 'sync' })
watch(sourceStamp, () => { sourceOptionsRead.invalidate(); sourceOptionsLoading.value = false; sourceOptions.value = []; sourceNextPage.value = null }, { flush: 'sync' })
watch(creating, value => { if (!value) hashAbort?.abort() })
watch([() => session.current?.subjectId, canRead, canAudit], ([subject, allowed], previous) => {
  for (const scope of readScopes) scope.invalidate()
  loading.value = false; detailLoading.value = false; evidenceLoading.value = false; auditLoading.value = false; syncLoading.value = false
  audits.value = []; auditNext.value = null
  if (!allowed || subject !== previous?.[0]) {
    clearDetail(); items.value = []; nextCursor.value = null; cursors.value = ['']; source.value = null; syncWork.value = null; software.value = null; maxBytes.value = null
  }
}, { flush: 'sync' })
onMounted(async () => {
  window.addEventListener('beforeunload', unload)
  const ticket = startRead(initialRead), initialChannel = channel.value
  try {
    if (!await loadSource() || !current(ticket)) return
    if (source.value?.kind === 'GitLab' && route.query.channel !== 'Test' && channel.value === initialChannel) channel.value = 'Formal'
    const caps = await read<{ maxPackageBytes: number }>('/api/v1/manage/capabilities', ticket)
    if (!current(ticket)) return
    maxBytes.value = caps.maxPackageBytes; await load()
    if (current(ticket) && typeof route.query.releaseId === 'string') await detail(route.query.releaseId)
  } catch (e) { if (current(ticket)) await failed(e) }
  finally { ticket.finish(); if (!disposed && canRead.value) poll = setTimeout(tick, 5000) }
})
onUnmounted(() => { disposed = true; for (const scope of readScopes) scope.invalidate(); globalPending.value = false; hashAbort?.abort(); uploadAbort?.abort(); file.value = null; window.removeEventListener('beforeunload', unload); if (poll) clearTimeout(poll) })
</script>
<template>
  <RouterLink v-if="!isPlc && (session.can('deployment.create',softwareId) || session.can('instance.read',softwareId))" class="text-link" :to="{ path: '/deployments', query: { softwareId, releaseId: selected?.state === 'Formal' ? selected.id : undefined } }">正式版本更新投放</RouterLink>
  <section class="workspace">
    <div class="page-heading"><div><div class="eyebrow">软件版本</div><h1>{{ software?.name ?? '版本与安装包' }}</h1><p v-if="software" class="muted">{{ software.code }} · {{ categoryName(software.category) }} · 同软件设备共用版本库</p></div><button v-if="canUpload" class="primary" :disabled="locked || !maxBytes" @click="begin">登记版本并上传</button></div>
    <p v-if="failure" class="error" role="alert">{{ failure }}</p><p v-if="notice" class="success" role="status">{{ notice }}</p>
    <OperationStatus :mutation="mutation" />
    <p v-if="!canRead" class="state-card">当前账号无权查看此软件版本，请核对当前权限。</p>
    <div v-if="uncertain && !selected" class="state-card"><p>原操作仍待核实；查看权限变化不会清除原键和原正文。</p><button :disabled="busy" @click="verifyOriginal">核实原操作</button></div>
    <section v-if="source?.kind === 'GitLab'" class="panel gitlab-source" aria-label="GitLab 正式版本来源">
      <h2>GitLab 正式版本</h2><p>研发测试与正式发布在 GitLab 完成。平台核对来源和双副本后开放下载，设备投放由人员另行创建。</p>
      <p class="muted">{{ source.baseUri }} · 项目 {{ source.projectId }} · 包 {{ source.packageName }} · {{ source.enabled ? '同步已启用' : '同步未启用' }}</p>
      <p v-if="source.configurationAvailable === false" class="error">来源配置当前不可用，请由部署人员核对连接及允许的项目；已有版本和历史仍保留。</p>
      <p v-if="source.lastSyncedAt">最近同步核对：{{ new Date(source.lastSyncedAt).toLocaleString() }}</p><p v-if="source.lastErrorCode" class="error">{{ source.lastErrorCode }}</p>
      <form v-if="canSync || uncertain && pendingAction === 'sync'" @submit.prevent="synchronize"><label>历史 Tag（留空扫描正式发布）<input v-model="historicalTag" maxlength="34" placeholder="v1.2.3" :disabled="locked" /></label><button class="primary" :disabled="busy || uncertain && pendingAction !== 'sync'">{{ uncertain ? '核实原同步操作' : '立即同步' }}</button></form>
      <p v-if="error" class="error">{{ error }}</p><p v-if="uncertain" class="muted">原操作键和正文保留在本页内存中，请人工核实，页面不会自动重发。</p>
      <div v-if="syncWork"><p>{{ syncWork.tag ?? '发布扫描' }} · {{ syncStages[syncWork.stage] ?? syncWork.stage }}</p><p v-if="syncWork.lastErrorCode" class="error">{{ syncWork.lastErrorCode }}</p><p v-if="syncWork.stage === 'Completed'" class="muted">同步已完成；可刷新版本列表或查看导入版本，当前查看对象不会自动切换。</p><button :disabled="busy || syncLoading" @click="refreshSync">核对同步进度</button><button v-if="syncWork.releaseId" :disabled="locked" @click="detail(syncWork.releaseId)">查看导入版本</button></div>
    </section>
    <p v-if="isPlc" class="state-card">PLC 人工归档：上传不透明程序包，双副本核对后开放测试。人员现场验证后转正式；下载不改变控制器使用版本。</p>
    <p v-if="source?.kind === 'Manual'" class="state-card" aria-label="版本来源配置">尚未绑定 GitLab。来源管理员可选择允许的项目及安装包；现有人工版本与历史继续保留。</p>
    <div class="dialog-actions"><button v-if="canManageSource" :disabled="locked || sourceOptionsLoading" @click="openSource">{{ source?.kind === 'GitLab' ? '管理 GitLab 来源' : '关联 GitLab 来源' }}</button></div>
    <dialog v-if="sourceDraft" ref="sourceDialog" @cancel.prevent="closeSource" aria-labelledby="source-dialog-title">
      <h2 id="source-dialog-title">GitLab 来源设置</h2><p>{{ sourceDraft.softwareName }} · 修订 {{ sourceDraft.revision }}</p>
      <p v-if="!sourceDraftValid" class="error" role="alert">人员或权限已变化，请重新打开表单；原未知操作仍可人工核实。</p>
      <p v-if="sourceDraft.blocking.length" class="muted">有 {{ sourceDraft.blocking.length }} 项未结束工作，当前可启停同步；完成后才能更换来源。</p>
      <form @submit.prevent="saveSource"><fieldset :disabled="locked || !sourceDraftValid">
        <label>查找允许的来源<input v-model="sourceSearch" maxlength="200" placeholder="连接、项目或安装包" /></label><div class="dialog-actions"><button type="button" :disabled="sourceOptionsLoading" @click="fetchSourceOptions(1, sourceSearch)">查询来源</button></div>
        <div class="table-scroll" tabindex="0" role="region" aria-label="允许的 GitLab 来源"><table><thead><tr><th>连接</th><th>项目</th><th>安装包</th><th>选择</th></tr></thead><tbody><tr v-for="option in sourceOptions" :key="`${option.connectionId}/${option.projectId}/${option.packageName}`"><td>{{ option.connectionName }}</td><td>{{ option.projectName }}</td><td>{{ option.packageName }}</td><td><button type="button" @click="sourceDraft.selected = option">选择此来源</button></td></tr></tbody></table></div>
        <p v-if="!sourceOptions.length && !sourceOptionsLoading" class="muted">没有匹配的允许来源，请调整搜索或由部署管理员配置连接及项目范围。</p><p v-if="sourceOptionsLoading" role="status">正在读取来源候选…</p>
        <button v-if="sourceNextPage" type="button" :disabled="sourceOptionsLoading" @click="fetchSourceOptions(sourceNextPage, sourceAppliedSearch)">更多来源</button>
        <dl v-if="sourceDraft.selected"><dt>已选连接</dt><dd>{{ sourceDraft.selected.connectionName }}</dd><dt>项目</dt><dd>{{ sourceDraft.selected.projectName }}</dd><dt>安装包</dt><dd>{{ sourceDraft.selected.packageName }}</dd></dl>
        <label class="check"><input v-model="sourceDraft.enabled" type="checkbox" />启用自动同步</label><label>修改原因<textarea v-model="sourceDraft.reason" required maxlength="256" /></label>
      </fieldset><p class="muted">更换来源保留历史版本及机台安装事实，不自动更新设备。</p><p v-if="error" class="error" role="alert">{{ error }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p>
      <OperationStatus :mutation="mutation" /><div class="dialog-actions"><button type="button" :disabled="locked" @click="closeSource">取消</button><button class="primary" :disabled="busy || !uncertain && (!sourceDraftValid || !sourceDraft.selected || !sourceDraft.reason.trim())">{{ uncertain ? '核实原来源操作' : '保存来源' }}</button></div></form>
    </dialog>
    <div class="tab-row"><button :aria-pressed="channel === 'Test'" :disabled="locked" @click="channel = 'Test'; changeChannel()">测试版本</button><button :aria-pressed="channel === 'Formal'" :disabled="locked" @click="channel = 'Formal'; changeChannel()">正式版本</button></div>
    <div class="dialog-actions"><button :disabled="locked || loading" @click="load(true)">刷新列表</button><button v-if="software?.latestAvailableFormalReleaseId" :disabled="locked" @click="latestFormal">查看最新可用正式版</button></div>
    <div class="account-layout">
      <section class="panel"><div v-if="loading" class="empty" role="status">正在读取版本…</div><div v-else-if="!items.length" class="empty">{{ channel === 'Formal' ? (source?.kind === 'GitLab' ? '尚无可用的 GitLab 正式版本，请查看同步进度。' : '尚无正式版本。完成测试并转正式后将在此展示。') : '没有可查看的测试版本。' }}</div>
        <div v-else class="table-scroll" tabindex="0" role="region" aria-label="可横向滚动的数据表格"><table><thead><tr><th>版本</th><th>状态</th><th>更新内容</th><th>登记时间</th></tr></thead><tbody><tr v-for="item in items" :key="item.id" :class="{ selected: selected?.id === item.id }"><td><button class="text-button" :disabled="locked" @click="detail(item.id)">{{ item.version }}</button></td><td>{{ releaseState(item.state) }}</td><td>{{ item.changeSummary }}</td><td>{{ new Date(item.createdAt).toLocaleString() }}</td></tr></tbody></table></div>
        <footer class="pagination"><span>本页可查看 {{ items.length }} 项</span><div><button :disabled="loading || locked || cursors.length <= 1" @click="previous">上一页</button><button :disabled="loading || locked || !nextCursor" @click="next">下一页</button></div></footer>
      </section>
      <aside class="panel detail" aria-label="版本详情" :aria-busy="detailLoading"><p v-if="detailLoading" role="status">正在核对当前版本详情…</p><template v-if="selected"><h2>{{ selected.version }} <span class="badge">{{ releaseState(selected.state) }}</span></h2><p class="description">{{ selected.changeSummary }}</p><dl><div><dt>变更级别</dt><dd>{{ selected.changeLevel }}</dd></div><div><dt>变更原因</dt><dd>{{ selected.changeReason }}</dd></div><div><dt>修订</dt><dd>{{ selected.revision }}</dd></div><div v-if="selected.disabledAt"><dt>停用原因</dt><dd>{{ selected.disableReason }}</dd></div></dl>
        <template v-if="pkg"><h3>安装包</h3><dl><div><dt>处理阶段</dt><dd>{{ packageStage(pkg.processingStage) }}</dd></div><div><dt>已核对副本</dt><dd>{{ pkg.healthyReplicaCount }} / 2</dd></div><div><dt>大小</dt><dd>{{ pkg.sizeBytes ?? '尚未确认' }} 字节（预期 {{ pkg.expectedSize }}）</dd></div><div><dt>SHA-256</dt><dd class="digest">{{ pkg.sha256 ?? '尚未确认' }}</dd></div></dl><p v-if="pkg.lastErrorCode" class="error">{{ pkg.lastErrorCode }}</p><a v-if="pkg.downloadAvailable && pkg.downloadPath" class="primary text-link" :href="isPlc && typeof route.query.bindingId==='string' ? plcDownload(route.query.bindingId,pkg.id) : pkg.downloadPath" download>下载安装包</a><p v-else class="muted">当前不可下载。首次开放下载须完成两个副本核对。</p>
          <button v-if="canUpload && selected.state === 'Staging' && ['AwaitingUpload', 'UploadFailed', 'Receiving'].includes(pkg.processingStage)" :disabled="locked" @click="resume">重传原版本安装包</button>
          <label v-if="canDisable && selected.state !== 'Disabled' || canUpload && pkg.processingStage === 'CopyFailed'">操作原因<input v-model="actionReason" maxlength="256" :disabled="locked" /></label>
          <div class="dialog-actions"><button v-if="canUpload && pkg.processingStage === 'CopyFailed'" :disabled="busy || !actionReason || uncertain && pendingAction !== 'retry'" @click="retry">{{ uncertain ? '核实原操作' : '重试副本工作' }}</button><button v-if="canDisable && selected.state !== 'Disabled'" :disabled="busy || !actionReason || uncertain && pendingAction !== 'disable'" @click="disable">{{ uncertain ? '核实原操作' : '停用版本' }}</button><button v-if="error && !uncertain" @click="refresh">加载最新详情</button></div><p v-if="error" class="error">{{ error }}</p><p v-if="uncertain" class="muted">结果待核实；原请求及操作键保留在当前页面内存中。</p>
        </template>
        <section v-if="selected.publishedAt && selected.sourceKind !== 'GitLab'" class="publication-history" aria-labelledby="publication-history-title"><h3 id="publication-history-title">正式发布记录</h3><dl><div><dt>发布工号</dt><dd>{{ selected.publishedEmployeeNo }}</dd></div><div><dt>发布时间</dt><dd>{{ new Date(selected.publishedAt).toLocaleString() }}</dd></div><div><dt>通过原因</dt><dd>{{ selected.publishReason }}</dd></div><div><dt>测试结论</dt><dd class="conclusion">{{ selected.publishConclusion }}</dd></div><div><dt>证据标识</dt><dd>{{ selected.plcEvidenceId ?? selected.testEvidenceId }}</dd></div></dl></section>
        <section v-if="!isPlc && (selected.state === 'Test' && canPublish || uncertain && pendingAction === 'publish')" class="publication-panel" aria-labelledby="publication-title"><h3 id="publication-title">测试转正式</h3><p class="muted">选择此版本的真实安装证据并填写结论，工号和时间由平台记录。</p><p v-if="!publishReady" class="muted">发布需两个当前健康副本；请等待核对或修复后刷新详情。</p><p v-if="!evidence.length" class="muted">尚无可选择的安装证据，不能发布。</p><form @submit.prevent="publish"><fieldset :disabled="locked"><label>安装证据<select v-model="chosenEvidence" required><option value="" disabled>选择安装上报</option><option v-for="item in evidence" :key="item.id" :value="item.id">{{ item.installedVersion }} · {{ item.instanceId }} · {{ new Date(item.receivedAt).toLocaleString() }} · {{ item.reportedRunningState }}</option></select></label><label>测试通过原因<input v-model="publishReason" required maxlength="256" /></label><label>测试结论<textarea v-model="publishConclusion" required maxlength="2000" rows="3" /></label></fieldset><button class="primary" :disabled="busy || uncertain && pendingAction !== 'publish' || !uncertain && (!canPublish || !publishReady || !chosenEvidence || !publishReason.trim() || !publishConclusion.trim())">{{ uncertain ? '核实原发布操作' : '转为正式版' }}</button></form></section>
        <section v-if="selected.sourceKind === 'GitLab' && selected.source"><h3>GitLab 发布来源</h3><dl><div><dt>正式 Tag</dt><dd>{{ selected.source.manifest.tag }}</dd></div><div><dt>提交 SHA</dt><dd class="digest">{{ selected.source.manifest.commitSha }}</dd></div><div><dt>源端发布时间</dt><dd>{{ new Date(selected.source.releasedAt).toLocaleString() }}</dd></div><div><dt>构建流水线</dt><dd><a :href="selected.source.pipelineUrl" target="_blank" rel="noopener noreferrer">{{ selected.source.manifest.pipelineId }}</a> · Job {{ selected.source.manifest.jobId }}</dd></div></dl><p class="muted">无需平台再次测试转正式，实际安装情况仍以实例上报为准。</p></section>
        <PlcPublication v-if="isPlc" :key="selected.id" :release="selected" :ready="publishReady" @published="channel='Formal';load(true);detail($event.id)" />
        <template v-else><h3>{{ selected.sourceKind === 'GitLab' ? '安装上报事实' : '测试安装证据' }}</h3><p class="muted">下载完成不代表安装成功；证据来自实例引用此版本的有效上报。</p><ul v-if="evidence.length" class="permission-list"><li v-for="item in evidence" :key="item.id">{{ item.installedVersion }} · {{ item.instanceId }}<small>{{ new Date(item.receivedAt).toLocaleString() }} · {{ item.reportedRunningState }}</small></li></ul><p v-else class="muted">尚无关联安装证据。</p><button v-if="evidenceNext" :disabled="evidenceLoading || locked" @click="moreEvidence">更多安装证据</button></template>
        <template v-if="canAudit"><h3>下载传输记录（已加载）</h3><ul v-if="audits.length" class="permission-list"><li v-for="item in audits" :key="item.requestId">{{ item.employeeNo ?? item.actorKind }} · {{ item.state }}<small>{{ item.bytesSent ?? '尚未取得可信结束记录' }} 字节 · {{ item.startedAt }}</small></li></ul><p v-else class="muted">已加载数据中无此版本的下载记录。</p><button v-if="auditNext" :disabled="auditLoading || locked" @click="moreAudit()">更多下载记录</button></template>
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
.gitlab-source { padding: 1.5rem; display: grid; gap: .75rem; overflow-wrap: anywhere; }
.gitlab-source h2, .gitlab-source p { margin: 0; }
.gitlab-source form { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: end; gap: .75rem; max-width: 44rem; }
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
