<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { request, ApiError } from '../api'
import { useSession } from '../session'
import { useOwnedReads } from '../ownedReads'
import { useProtectedMutation } from '../protectedMutation'
import type { Release } from '../packages'
import { type Installation, type WorkspacePage, queryString, mergeRows } from '../hardware'
import type { PlcEvidence } from '../plc'
import OperationStatus from './OperationStatus.vue'
const props=defineProps<{release:Release;ready:boolean}>(), emit=defineEmits<{published:[value:Release]}>()
const session=useSession(),route=useRoute(),mutation=useProtectedMutation()
const rows=ref<Installation[]>([]),next=ref<string|null>(null),selected=ref(''),failure=ref(''),evidence=ref<PlcEvidence|null>(null)
const fieldAt=ref(''),verification=ref(''),materials=ref(''),reason=ref(''),conclusion=ref(''),stamp=ref('')
const currentStamp=computed(()=>JSON.stringify([session.current?.subjectId,session.access]))
const reads=useOwnedReads(()=>{rows.value=[];next.value=null;evidence.value=null;selected.value='';stamp.value='';fieldAt.value='';verification.value='';materials.value='';reason.value='';conclusion.value=''})
const allowed=computed(()=>session.can('release.publish',props.release.softwareId)),valid=computed(()=>stamp.value===currentStamp.value)
const targets=computed(()=>rows.value.filter(r=>r.position&&session.canAt('plc.verify',r.software.id,r.device.processId,r.device.id)))
async function load(cursor?:string|null){if(!allowed.value||!session.canAssets)return;await reads.run('targets',!cursor,s=>request<WorkspacePage<Installation>>('/api/v1/manage/workspace/installations?'+queryString({softwareId:props.release.softwareId},cursor),'GET',undefined,undefined,undefined,{signal:s}),p=>{rows.value=cursor?mergeRows(rows.value,p.items,x=>x.rowId):p.items;next.value=p.nextCursor;stamp.value=currentStamp.value;if(!cursor&&targets.value.some(r=>r.position!.id===route.query.bindingId))selected.value=String(route.query.bindingId)},e=>{failure.value=e instanceof Error?e.message:'控制器读取失败'})}
async function publish(){
  const r=targets.value.find(r=>r.position!.id===selected.value)
  if(!mutation.uncertain.value&&(!r||!valid.value||!props.ready||!allowed.value||!Number.isFinite(Date.parse(fieldAt.value))))return
  const result=await mutation.perform<Release>(`/api/v1/manage/releases/${props.release.id}/plc-publish`,'POST',{expectedRevision:props.release.revision,bindingId:r?.position?.id,bindingRevision:r?.position?.revision,fieldAt:fieldAt.value?new Date(fieldAt.value).toISOString():null,verification:verification.value,materials:materials.value,publishReason:reason.value,publishConclusion:conclusion.value},session.current!.csrfToken)
  if(result&&mutation.completed.value)emit('published',result)
}
onMounted(async()=>{
  if(props.release.publishedAt)await reads.run('evidence',true,s=>request<PlcEvidence|null>(`/api/v1/manage/releases/${props.release.id}/plc-evidence`,'GET',undefined,undefined,undefined,{signal:s}),v=>{evidence.value=v},e=>{failure.value=e instanceof ApiError&&[403,404].includes(e.status)?'验证现场详情受现场查看权限限制。':e instanceof Error?e.message:'验证记录读取失败'})
  else await load()
})
</script>
<template>
<section class="publication-panel"><h3>{{ release.publishedAt ? 'PLC 现场验证' : '人员验证转正式' }}</h3><p class="muted">验证仅说明此程序库满足本次发布依据，不代表其他控制器已经使用或验证。</p>
<p v-if="failure" class="error" role="alert">{{ failure }}</p><dl v-if="evidence"><dt>现场时间</dt><dd>{{ new Date(evidence.fieldAt).toLocaleString() }}</dd><dt>验证记录</dt><dd>{{ evidence.verification }}</dd><dt>材料</dt><dd>{{ evidence.materials }}</dd></dl>
<form v-if="release.state==='Test' && allowed || mutation.uncertain.value" @submit.prevent="publish"><fieldset :disabled="mutation.blocked.value || !valid"><label>验证控制器<select v-model="selected" required><option value="" disabled>选择有验证权限的控制器</option><option v-for="r in targets" :key="r.rowId" :value="r.position!.id">{{ r.device.name }} / {{ r.hardware?.name }}</option></select></label><button v-if="next" type="button" :disabled="reads.busy.value" @click="load(next)">更多控制器</button><label>现场验证时间<input v-model="fieldAt" type="datetime-local" required /></label><label>验证记录<textarea v-model="verification" required maxlength="2000" /></label><label>材料说明或引用<textarea v-model="materials" required maxlength="2000" /></label><label>发布原因<input v-model="reason" required maxlength="256" /></label><label>发布结论<textarea v-model="conclusion" required maxlength="2000" /></label></fieldset>
<p v-if="!ready" class="muted">等待两个健康副本核对完成后方可发布。</p><p v-if="!valid" class="muted">请重新读取当前权限和控制器后填写。</p><button v-if="!valid" type="button" @click="load()">重新读取控制器</button><OperationStatus :mutation="mutation" /><p v-if="mutation.error.value" class="error">{{ mutation.error.value }}</p><button class="primary" :disabled="mutation.busy.value || !mutation.uncertain.value && (!valid || !ready || !selected)">{{ mutation.uncertain.value ? '核实原发布操作' : '验证通过，转正式' }}</button></form>
</section>
</template>
<style scoped>
section{margin-top:1.5rem;border-top:1px solid #dde5ef;padding-top:1rem}fieldset{border:0;padding:0;display:grid;gap:1rem}dd{white-space:pre-wrap;overflow-wrap:anywhere}button{margin-top:.75rem}
</style>
