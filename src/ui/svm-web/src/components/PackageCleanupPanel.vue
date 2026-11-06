<script setup lang="ts">
import { computed, inject, nextTick, onUnmounted, ref, watch } from 'vue'
import { ApiError, request } from '../api'
import { createReadScope } from '../reading'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
import { pendingWriteKey } from '../mutation'
import type { Release } from '../packages'
import OperationStatus from './OperationStatus.vue'

interface DownloadGroup { nodeId: string; workerGeneration: string; openCount: number; latestStartedAt: string }
interface Status { packageId: string; revision: number; state: string; workId: string | null; blockingReasons: string[]; openDownloads: DownloadGroup[]; moreDownloadGenerations: boolean }
interface Work { id: string; state: string; stage: string; completedItems: number; totalItems: number; lastErrorCode: string | null }
interface Draft { kind: 'cleanup' | 'stop'; packageId: string; version: string; revision: number; stamp: string; group?: DownloadGroup }
const props = defineProps<{ release: Release }>(), emit = defineEmits<{ changed: [] }>()
const session = useSession(), mutation = useProtectedMutation(), pending = inject(pendingWriteKey, ref(false))
const canClean = computed(() => props.release.state === 'Disabled' && session.can('package.clean', props.release.softwareId))
const stamp = computed(() => JSON.stringify([session.current?.subjectId, session.current?.authorizationRevision, session.access, session.current?.permissions]))
const locked = computed(() => pending.value || mutation.blocked.value)
const status = ref<Status | null>(null), work = ref<Work | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const reads = createReadScope(), opened = ref(false), readAt = ref(''), draft = ref<Draft | null>(null), dialog = ref<HTMLDialogElement | null>(null)
const reason = ref(''), evidence = ref(''), stoppedAt = ref(''), confirmed = ref(false)
const validDraft = computed(() => canClean.value && draft.value?.stamp === stamp.value && draft.value.packageId === props.release.packageId)
const blockers: Record<string,string> = { RELEASE_NOT_DISABLED:'版本尚未停用', FILE_WORK_ACTIVE:'仍有上传或修复工作', TASKS_UNFINISHED:'仍有未结束的投放或任务', DOWNLOADS_OPEN:'仍有下载未确认结束' }
let disposed = false, timer: ReturnType<typeof setTimeout> | undefined, returnFocus: HTMLElement | null = null
const get = <T,>(path: string, signal: AbortSignal) => request<T>(path, 'GET', undefined, undefined, undefined, { signal })
async function load() {
  if (disposed || !canClean.value || loading.value) return
  reads.invalidate(); const ticket = reads.capture(), id = props.release.packageId, owner = stamp.value
  const owns = () => !disposed && ticket.current() && id === props.release.packageId && stamp.value === owner && canClean.value
  loading.value = true
  try {
    const s = await get<Status>(`/api/v1/manage/packages/${id}/cleanup`, ticket.signal)
    const w = s.workId ? await get<Work>(`/api/v1/manage/package-work/${s.workId}`, ticket.signal) : null
    if (owns()) {
      const changed = status.value?.state !== s.state && ['Cleaning','Cleaned'].includes(s.state)
      status.value = s; work.value = w; readAt.value = new Date().toLocaleTimeString(); failure.value = ''
      if (changed) emit('changed')
    }
  } catch(e) {
    if (owns() && !(e instanceof ApiError && e.cancelled)) {
      failure.value = e instanceof Error ? e.message : '清理条件读取失败。'
      if (e instanceof ApiError && [401,403,404].includes(e.status)) await session.load()
    }
  } finally { if (owns()) loading.value = false; ticket.finish() }
}
async function poll() {
  if (disposed) return
  if (opened.value && !locked.value && !draft.value && work.value && ['Pending','Running'].includes(work.value.state)) await load()
  timer = setTimeout(poll, 5000)
}
async function toggle(event: Event) {
  opened.value = (event.target as HTMLDetailsElement).open
  if (opened.value) { await load(); if (!timer && !disposed) timer = setTimeout(poll,5000) }
}
async function open(kind: 'cleanup' | 'stop', group?: DownloadGroup) {
  if (locked.value || !canClean.value || !status.value || failure.value || loading.value) return
  if (kind === 'cleanup' && (status.value.state === 'Cleaned' || status.value.blockingReasons.length || work.value && ['Pending','Running'].includes(work.value.state))) return
  returnFocus = document.activeElement as HTMLElement | null
  draft.value = { kind, packageId: props.release.packageId, version: props.release.version, revision: status.value.revision, stamp: stamp.value, group: group ? {...group} : undefined }
  reason.value = evidence.value = stoppedAt.value = ''; confirmed.value = false; mutation.error.value = ''
  await nextTick(); dialog.value?.showModal()
}
function close() {
  if (locked.value) return
  dialog.value?.close(); draft.value = null; returnFocus?.focus()
}
async function save() {
  const d = draft.value
  if (!d || !session.current || mutation.busy.value) return
  if (!mutation.uncertain.value && (locked.value || !validDraft.value || !confirmed.value || !reason.value.trim())) return
  if (!mutation.uncertain.value && d.kind === 'stop' && (!d.group || !evidence.value.trim() || !stoppedAt.value || !Number.isFinite(Date.parse(stoppedAt.value)))) return
  const original = mutation.pending.value
  const path = original?.path ?? `/api/v1/manage/packages/${d.packageId}/${d.kind === 'cleanup' ? 'cleanup' : 'download-stop-evidence'}`
  const body = original?.body ?? (d.kind === 'cleanup' ? { reason: reason.value, expectedRevision: d.revision } : {
    nodeId: d.group!.nodeId, workerGeneration: d.group!.workerGeneration, stoppedAt: new Date(stoppedAt.value).toISOString(),
    processStoppedConfirmed: true, evidence: evidence.value, reason: reason.value, expectedRevision: d.revision })
  const result = await mutation.perform(path, 'POST', body, session.current.csrfToken)
  if (result && mutation.completed.value) {
    notice.value = d.kind === 'cleanup' ? '清理工作已登记，等待两个节点确认。版本和使用历史继续保留。' : '已保存人工停止证据；这不表示下载成功，也不补写传输字节。'
    close(); await load(); if (d.kind === 'stop') emit('changed')
  } else if (!mutation.uncertain.value) {
    if (mutation.errorCode.value === 'REVISION_CONFLICT') failure.value = '包状态已变化，请取消后重新读取并核对。'
    else if (mutation.errorCode.value === 'INVALID_STATE') failure.value = '当前条件不允许清理或登记停止证据，请取消后重新核对。'
    await session.load()
  }
}
watch([() => props.release.packageId, stamp], () => {
  reads.invalidate(); loading.value = false; status.value = null; work.value = null; readAt.value = ''
  failure.value = '对象或权限已变化，请重新读取清理条件。'
}, { flush:'sync' })
onUnmounted(() => { disposed = true; reads.invalidate(); clearTimeout(timer) })
</script>
<template>
  <details v-if="canClean || mutation.uncertain.value" class="cleanup-panel" @toggle="toggle">
    <summary>清理停用版本的文件</summary>
    <p class="muted">仅删除厂内包文件；版本资料、安装和使用履历、下载及任务历史继续保留。删除后不能再从本平台下载此包。</p>
    <p v-if="failure" class="error" role="alert">{{ failure }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">正在核对清理条件…</p>
    <template v-if="status">
      <p v-if="status.state === 'Cleaned'" role="status">两个节点的包文件均已清理，历史已保留。</p>
      <ul v-if="status.blockingReasons.length" aria-label="清理阻塞原因"><li v-for="code in status.blockingReasons" :key="code">{{ blockers[code] ?? '需要进一步核实清理条件' }}</li></ul>
      <p v-if="work">清理进度：{{ work.completedItems }} / {{ work.totalItems }} 个节点 · {{ work.state === 'Completed' ? '完成' : work.state === 'Failed' ? '中断，可核实后继续' : '处理中' }}</p>
      <p v-if="work?.lastErrorCode" class="error">节点清理未完成（{{ work.lastErrorCode }}）。请核对节点可用性及当前权限。</p>
      <button v-if="status.state !== 'Cleaned'" :disabled="locked || loading || !!failure || !!status.blockingReasons.length || work?.state === 'Pending' || work?.state === 'Running'" @click="open('cleanup')">{{ work ? '继续清理剩余文件' : '清理包文件' }}</button>
      <details v-if="status.openDownloads.length" class="stop-evidence"><summary>下载结束记录缺失时的运维核实</summary>
        <p>先等待真实传输结束。只有实际确认对应网关进程已停止，才可登记节点代次及停止证据；超时不能代替确认。</p>
        <div v-for="group in status.openDownloads" :key="group.nodeId+group.workerGeneration" class="download-group">
          <p>{{ group.nodeId }} · {{ group.openCount }} 条下载未结束</p><small>进程代次 {{ group.workerGeneration }} · 最近开始 {{ new Date(group.latestStartedAt).toLocaleString() }}</small>
          <button :disabled="locked || loading || !!failure" @click="open('stop',group)">登记已停止证据</button>
        </div><p v-if="status.moreDownloadGenerations">还有其他节点代次；处理当前记录后重新查询。</p>
      </details>
    </template>
    <div class="actions"><button :disabled="locked || loading || !canClean" @click="load">重新核对清理条件</button><small v-if="readAt">读取于 {{ readAt }}</small></div>
    <dialog ref="dialog" aria-labelledby="cleanup-title" @cancel.prevent="close">
      <form v-if="draft" @submit.prevent="save">
        <h3 id="cleanup-title">{{ draft.kind === 'cleanup' ? '确认清理' : '登记网关停止证据' }} · {{ draft.version }}</h3>
        <p v-if="!validDraft" class="error">对象或权限已变化，不能提交原草稿；结果未知时仍须保留原操作核实。</p>
        <fieldset :disabled="locked || !validDraft">
          <template v-if="draft.kind === 'stop'">
            <p>节点 {{ draft.group?.nodeId }} · 代次 {{ draft.group?.workerGeneration }}</p>
            <label>实际停止时间<input v-model="stoppedAt" type="datetime-local" required step="1" /></label>
            <label>停止证据<textarea v-model="evidence" required maxlength="2000" /></label>
          </template>
          <label>操作原因<input v-model="reason" required maxlength="256" /></label>
          <label class="confirmation"><input v-model="confirmed" type="checkbox" required />{{ draft.kind === 'cleanup' ? '已确认永久删除两个节点的文件，并保留历史记录。' : '已实际确认上述节点代次的网关进程停止，不以超时或日志缺失推断。' }}</label>
        </fieldset>
        <p v-if="mutation.error.value" class="error" role="alert">{{ mutation.error.value }}</p>
        <OperationStatus :mutation="mutation" />
        <div class="actions"><button type="button" :disabled="locked" @click="close">取消</button>
          <button :disabled="mutation.busy.value || !mutation.uncertain.value && (!validDraft || !confirmed || !reason.trim())">{{ mutation.uncertain.value ? '核实原操作' : draft.kind === 'cleanup' ? '确认清理文件' : '保存停止证据' }}</button></div>
      </form>
    </dialog>
  </details>
</template>
<style scoped>
.cleanup-panel { margin-top:1rem; border-top:1px solid #dde5ef; padding-top:1rem; }
summary { cursor:pointer; font-weight:600; } .download-group { padding:.5rem 0; border-bottom:1px solid #edf0f5; }
fieldset { border:0; padding:0; display:grid; gap:.75rem; } .confirmation { display:flex; align-items:flex-start; gap:.5rem; }
.confirmation input { width:auto; } .stop-evidence { margin:1rem 0; } small { overflow-wrap:anywhere; }
</style>
