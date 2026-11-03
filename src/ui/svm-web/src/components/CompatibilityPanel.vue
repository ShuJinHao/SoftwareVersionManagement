<script setup lang="ts">
import { computed, inject, onUnmounted, ref, watch } from 'vue'
import { request, ApiError } from '../api'
import { createReadScope } from '../reading'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
import { pendingWriteKey } from '../mutation'
import { type Release } from '../packages'
import { type Page } from '../catalog'
import { type Compatibility, lines } from '../tasks'
import OperationStatus from './OperationStatus.vue'

const props = defineProps<{ release: Release }>(), session = useSession(), mutation = useProtectedMutation()
const pending = inject(pendingWriteKey, ref(false)), locked = computed(() => pending.value || mutation.blocked.value)
const canRead = computed(() => session.can('software.read', props.release.softwareId))
const canWrite = computed(() => session.can('release.upload', props.release.softwareId) && props.release.state === 'Formal')
const stamp = computed(() => JSON.stringify([session.current?.subjectId,session.current?.authorizationRevision,session.current?.authorization,session.current?.permissions]))
const reads = createReadScope(), historyRead = createReadScope()
const history = ref<Compatibility[]>([]), next = ref<string | null>(null), candidates = ref<Release[]>([]), candidateNext = ref<string | null>(null)
const loading = ref(false), candidatesLoading = ref(false), failure = ref(''), notice = ref(''), editing = ref(false)
const draft = ref<{ releaseId: string; version: string; stamp: string } | null>(null)
const sourceIds = ref<string[]>([]), databaseMode = ref('None'), requirements = ref([{ databaseKey: '', schemas: '' }]), statement = ref(''), reason = ref('')
let disposed = false
const validDraft = computed(() => draft.value?.releaseId === props.release.id && draft.value.stamp === stamp.value && canWrite.value)
const get = <T,>(path: string, signal: AbortSignal) => request<T>(path,'GET',undefined,undefined,undefined,{ signal })
async function load(cursor?: string | null) {
  if (!canRead.value || loading.value) return
  if (!cursor) historyRead.invalidate()
  const id=props.release.id, owner=stamp.value, ticket=historyRead.capture(); loading.value=true
  try { const p=await get<Page<Compatibility>>(`/api/v1/manage/releases/${id}/compatibility-declarations${cursor ? '?cursor='+encodeURIComponent(cursor) : ''}`, ticket.signal)
    if (!disposed && ticket.current() && props.release.id===id && stamp.value===owner && canRead.value) {history.value=cursor ? [...history.value,...p.items] : p.items; next.value=p.nextCursor} }
  catch(e) {if(ticket.current() && !(e instanceof ApiError && e.cancelled)) failure.value=e instanceof Error ? e.message : '兼容依据读取失败。'}
  finally {ticket.finish(); if(ticket.current()) loading.value=false}
}
async function loadCandidates(cursor?: string | null) {
  if (!validDraft.value || candidatesLoading.value) return
  const id=props.release.id, owner=stamp.value, ticket=reads.capture(); candidatesLoading.value=true
  try { const p=await get<Page<Release>>(`/api/v1/manage/software/${props.release.softwareId}/releases?pageSize=50${cursor ? '&cursor='+encodeURIComponent(cursor) : ''}`,ticket.signal)
    if(!disposed && ticket.current() && props.release.id===id && owner===stamp.value && validDraft.value) {candidates.value=cursor ? [...candidates.value,...p.items] : p.items; candidateNext.value=p.nextCursor} }
  catch(e) {if(ticket.current() && !(e instanceof ApiError && e.cancelled)) failure.value=e instanceof Error ? e.message : '来源版本读取失败。'}
  finally {ticket.finish(); if(ticket.current()) candidatesLoading.value=false}
}
async function open() {
  if (locked.value || !canWrite.value) return
  reads.invalidate(); draft.value={releaseId:props.release.id,version:props.release.version,stamp:stamp.value}
  sourceIds.value=[]; databaseMode.value='None'; requirements.value=[{databaseKey:'',schemas:''}]; statement.value=''; reason.value=''
  candidates.value=[]; candidateNext.value=null; failure.value=''; editing.value=true; await loadCandidates()
}
async function save() {
  const original=mutation.pending.value
  if (!session.current || mutation.busy.value || !original && (!validDraft.value || locked.value)) return
  const id=draft.value?.releaseId; if(!id) return
  const body=original?.body ?? {sourceReleaseIds:[...sourceIds.value],databaseMode:databaseMode.value,
    databaseRequirements:databaseMode.value==='None' ? [] : requirements.value.map(r=>({databaseKey:r.databaseKey.trim(),acceptedSchemaIds:lines(r.schemas)})),statement:statement.value,reason:reason.value}
  const result=await mutation.perform<Compatibility>(original?.path ?? `/api/v1/manage/releases/${id}/compatibility-declarations`,'POST',body,session.current.csrfToken)
  if(result && mutation.completed.value) {editing.value=false; draft.value=null; notice.value='新兼容修订已登记。尚未开始的回退会按最新修订重新核验。'; await load()}
  else if(!mutation.uncertain.value) await session.load()
}
watch(stamp,()=>{reads.invalidate();historyRead.invalidate();loading.value=candidatesLoading.value=false;history.value=[];candidates.value=[];next.value=candidateNext.value=null;sourceIds.value=[];failure.value='授权已变化，请重新读取兼容依据。'}, {flush:'sync'})
onUnmounted(()=>{disposed=true;reads.invalidate();historyRead.invalidate()})
</script>
<template>
  <details v-if="canRead || mutation.uncertain.value" class="compatibility-panel" @toggle="($event.target as HTMLDetailsElement).open && load()">
    <summary>回退兼容依据</summary>
    <p class="muted">目标为 {{ release.version }}。声明允许的来源版本及完整数据库条件；不代表现场数据已备份。</p>
    <p v-if="failure || mutation.error.value" class="error" role="alert">{{ failure || mutation.error.value }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <OperationStatus :mutation="mutation" />
    <p v-if="loading" role="status">正在读取…</p><p v-else-if="!history.length">尚无兼容声明，不能向此版本发起回退。</p>
    <article v-for="item in history" :key="item.id"><h4>修订 {{ item.declarationRevision }} · {{ new Date(item.createdAt).toLocaleString() }}</h4><p>{{ item.statement }}</p><p>允许 {{ item.sourceReleaseIds.length }} 个来源版本 · {{ item.databaseMode==='None' ? '明确无本地数据库' : '须匹配全部数据库' }}</p><ul v-if="item.databaseMode==='Present'"><li v-for="r in item.databaseRequirements" :key="r.databaseKey">{{ r.databaseKey }}：{{ r.acceptedSchemaIds.join('、') }}</li></ul><p class="muted">登记原因：{{ item.reason }}</p></article>
    <button v-if="next" :disabled="loading || locked" @click="load(next)">更多兼容修订</button>
    <button v-if="canWrite && !editing" :disabled="locked" @click="open">登记新兼容修订</button>
    <form v-if="editing" @submit.prevent="save"><h4>允许回退至 {{ draft?.version }}</h4>
      <p v-if="!validDraft && !mutation.uncertain.value" role="alert">操作对象或授权已变化，请取消后重新核对。</p>
      <fieldset :disabled="locked || !validDraft"><legend>来源与数据库条件</legend>
        <label>允许的来源版本<select v-model="sourceIds" multiple required aria-label="允许的来源版本"><option v-for="r in candidates.filter(r=>r.id!==draft?.releaseId)" :key="r.id" :value="r.id">{{ r.version }} · {{ r.state }}</option></select></label>
        <button v-if="candidateNext" type="button" :disabled="candidatesLoading" @click="loadCandidates(candidateNext)">更多来源版本</button>
        <label>现场数据库<select v-model="databaseMode"><option value="None">明确无本地数据库</option><option value="Present">使用本地数据库</option></select></label>
        <template v-if="databaseMode==='Present'"><div v-for="(r,index) in requirements" :key="index" class="form-grid"><label>数据库标识<input v-model="r.databaseKey" required maxlength="64" /></label><label>允许的模式标识（每行一项）<textarea v-model="r.schemas" required maxlength="12800" /></label><button v-if="requirements.length>1" type="button" @click="requirements.splice(index,1)">移除条件</button></div><button type="button" :disabled="requirements.length>=16" @click="requirements.push({databaseKey:'',schemas:''})">添加数据库条件</button></template>
        <label>兼容说明<textarea v-model="statement" required maxlength="2000" /></label><label>登记原因<input v-model="reason" required maxlength="256" /></label>
      </fieldset>
      <div class="actions"><button type="button" :disabled="locked" @click="editing=false;draft=null">取消</button><button :disabled="mutation.busy.value || !mutation.uncertain.value && (!validDraft || !sourceIds.length)">{{ mutation.uncertain.value ? '核实原兼容声明' : '登记新修订' }}</button></div>
    </form>
  </details>
</template>
<style scoped>
.compatibility-panel { margin-top:1rem; border-top:1px solid #dde5ef; padding-top:1rem; }
summary { cursor:pointer; font-weight:600; } article { padding:.5rem 0; border-bottom:1px solid #edf0f5; }
fieldset { border:0; padding:0; display:grid; gap:.75rem; } select[multiple] { min-height:7rem; }
</style>
