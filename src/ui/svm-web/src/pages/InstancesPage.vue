<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, request } from '../api'
import { type Page, type Site } from '../catalog'
import { type Instance, type Credential, type History, type Grant, generateSecret, freshnessName, runningName, installationName, grantStateName, formatTime } from '../instances'
import { useSession } from '../session'
import OperationStatus from '../components/OperationStatus.vue'
import { useProtectedMutation } from '../protectedMutation'
import { useOwnedReads } from '../ownedReads'
const session = useSession(), route = useRoute(), mutation = useProtectedMutation()
const timeZone = ref('UTC')
const props = defineProps<{ contextual?: boolean }>()
const softwareId = ref(String(route.query.softwareId ?? '')), deviceNo = ref(''), reportedIp = ref(''), freshness = ref(''), running = ref(''), lifecycle = ref('')
const filters = ref<Record<string,string>>({}), rows = ref<Instance[]>([]), selected = ref<Instance | null>(null), failure = ref(''), notice = ref('')
const cursors=ref(['']), next=ref<string|null>(null), history=ref<History[]>([]), historyCursors=ref(['']), historyNext=ref<string|null>(null)
const credentials=ref<Credential[]>([]), credentialCursors=ref(['']), credentialNext=ref<string|null>(null)
type Mode = 'lifecycle' | 'credential' | 'recovery' | 'revokeRecovery'
interface Draft { instance: Instance; credential: Credential | null; grant: Grant | null; action: 'Active' | 'Suspended'; epoch: number }
const mode=ref<Mode|null>(null), draft=ref<Draft|null>(null), dialog=ref<HTMLDialogElement|null>(null)
const reason=ref(''), secret=ref(''), expires=ref(''), issued=ref<Grant|null>(null), issuedTarget=ref<Instance|null>(null)
let returnFocus: HTMLElement|null=null
function clearDetail() { selected.value=null; history.value=[]; credentials.value=[]; historyCursors.value=['']; credentialCursors.value=['']; historyNext.value=credentialNext.value=null }
const reads=useOwnedReads(() => {rows.value=[]; next.value=null; cursors.value=['']; clearDetail(); issued.value=null; issuedTarget.value=null; if(!mutation.uncertain.value) secret.value=''})
const loading=reads.busy
const draftValid=computed(() => !!draft.value && draft.value.epoch===reads.epoch.value && session.can(mode.value==='lifecycle' ? 'instance.manage' : 'enrollment.manage',draft.value.instance.softwareId))
const softwareIds=computed(() => session.current?.permissions.filter(p => p.operation==='instance.read' && p.softwareId).map(p=>p.softwareId!) ?? [])
const get=<T,>(path:string,signal:AbortSignal) => request<T>(path,'GET',undefined,undefined,undefined,{signal})
async function fail(e:unknown) {failure.value=e instanceof ApiError ? e.message : '读取失败，已保留原结果，请重试。'; if(e instanceof ApiError && [401,403,404].includes(e.status)) await session.load()}
function query(filters:Record<string,string>,stack:string[]) {const q=new URLSearchParams({...filters,pageSize:'50'}); const cursor=stack[stack.length-1]; if(cursor) q.set('cursor',cursor); return q}
async function list(reset=false, stack=cursors.value) {
  if(mutation.blocked.value) return
  const applied=reset ? Object.fromEntries(Object.entries({softwareId:softwareId.value,deviceNo:deviceNo.value,reportedIp:reportedIp.value,freshness:freshness.value,runningState:running.value,lifecycle:lifecycle.value}).filter(([,v])=>v)) : {...filters.value}
  if(!applied.softwareId || !session.can('instance.read',applied.softwareId)) return
  const proposed=reset ? [''] : [...stack]; failure.value=''
  if(reset) reads.invalidate('detail','history','credentials')
  await reads.run('list',reset,signal=>get<Page<Instance>>('/api/v1/manage/instances?'+query(applied,proposed),signal),p=>{rows.value=p.items; next.value=p.nextCursor; filters.value=applied; cursors.value=proposed; if(reset) clearDetail()},fail)
}
async function page(forward:boolean) {if(forward && next.value) await list(false,[...cursors.value,next.value]); else if(!forward && cursors.value.length>1) await list(false,cursors.value.slice(0,-1))}
async function detail(id:string) {
  if(mutation.blocked.value) return
  reads.invalidate('history','credentials'); clearDetail(); failure.value=''
  await reads.run('detail',true,async signal=>{
    const instance=await get<Instance>('/api/v1/manage/instances/'+id,signal)
    if(!session.can('instance.read',instance.softwareId)) throw new Error('实例权限已变化，请重新核实。')
    const [h,c]=await Promise.all([get<Page<History>>(`/api/v1/manage/instances/${id}/version-history?`+query({},['']),signal),session.can('enrollment.manage',instance.softwareId) ? get<Page<Credential>>(`/api/v1/manage/instances/${id}/credentials?`+query({},['']),signal) : Promise.resolve({items:[],nextCursor:null})])
    return {instance,h,c}
  },({instance,h,c})=>{selected.value=instance; history.value=h.items; historyNext.value=h.nextCursor; credentials.value=c.items; credentialNext.value=c.nextCursor},fail)
}
async function subPage(kind:'history'|'credentials',forward:boolean) {
  const instance=selected.value; if(!instance || mutation.blocked.value) return
  const stack=kind==='history' ? historyCursors : credentialCursors, cursor=kind==='history' ? historyNext : credentialNext
  if(forward && !cursor.value || !forward && stack.value.length<=1) return
  const proposed=forward ? [...stack.value,cursor.value!] : stack.value.slice(0,-1), id=instance.id
  await reads.run(kind,false,signal=>get<Page<History|Credential>>(`/api/v1/manage/instances/${id}/${kind==='history' ? 'version-history' : 'credentials'}?`+query({},proposed),signal),p=>{
    if(selected.value?.id!==id) return
    stack.value=proposed; cursor.value=p.nextCursor
    if(kind==='history') history.value=p.items as History[]; else credentials.value=p.items as Credential[]
  },fail)
}
async function open(value:Mode,c?:Credential) {
  const target=value==='revokeRecovery' ? issuedTarget.value : selected.value
  if(!target || mutation.blocked.value || !session.can(value==='lifecycle' ? 'instance.manage' : 'enrollment.manage',target.softwareId)) return
  draft.value={instance:{...target},credential:c ? {...c} : null,grant:issued.value ? {...issued.value} : null,action:target.lifecycle==='Active' ? 'Suspended' : 'Active',epoch:reads.epoch.value}
  mode.value=value; reason.value=''; secret.value=value==='recovery' ? generateSecret() : ''; expires.value=''; mutation.error.value=''
  returnFocus=document.activeElement as HTMLElement|null; await nextTick(); dialog.value?.showModal()
}
function close(clearIssued=true) {if(mutation.blocked.value) return; dialog.value?.close(); mode.value=null; draft.value=null; secret.value=''; if(clearIssued) {issued.value=null; issuedTarget.value=null}; returnFocus?.focus()}
async function save() {
  const d=draft.value
  if(!d || !mode.value || !mutation.uncertain.value && !draftValid.value) {mutation.error.value='人员或权限已变化，请关闭表单并重新核对。'; return}
  const id=d.instance.id; let path='',method='POST',body:object
  if(mode.value==='lifecycle') {path=`/api/v1/manage/instances/${id}/lifecycle`; method='PATCH'; body={lifecycle:d.action,expectedRevision:d.instance.revision,reason:reason.value}}
  else if(mode.value==='credential') {path=`/api/v1/manage/credentials/${d.credential!.id}/revoke`; body={expectedRevision:d.credential!.revision,reason:reason.value}}
  else if(mode.value==='revokeRecovery') {path=`/api/v1/manage/recovery-grants/${d.grant!.id}/revoke`; body={expectedRevision:d.grant!.revision,reason:reason.value}}
  else {path=`/api/v1/manage/instances/${id}/recovery-grants`; body={expiresAt:new Date(expires.value).toISOString(),secretMaterial:secret.value,reason:reason.value}}
  const currentMode=mode.value,result=await mutation.perform<Grant|Instance|Credential>(path,method,body,session.current?.csrfToken ?? '')
  if(mutation.completed.value && result) {
    notice.value='接入管理操作已完成。'; await session.load()
    if(currentMode==='recovery' && d.epoch===reads.epoch.value) {issued.value=result as Grant; issuedTarget.value=d.instance; dialog.value?.close(); mode.value=null; draft.value=null; returnFocus?.focus()}
    else {close(); if(session.can('instance.read',d.instance.softwareId)) await detail(id)}
  } else if(!mutation.uncertain.value) {secret.value=''; await session.load()}
}
onMounted(async()=>{
  if(session.canAssets) await reads.run('site',false,signal=>get<Site>('/api/v1/manage/site',signal),s=>{timeZone.value=s.siteTimeZone},fail)
  if(!softwareId.value) softwareId.value=softwareIds.value[0] ?? ''; if(!props.contextual) await list(true); if(route.query.id) await detail(String(route.query.id))
})
onUnmounted(()=>{secret.value=''})
</script>
<template>
  <section class="workspace"><div class="page-heading"><div><div class="eyebrow">现场软件</div><h1>软件实例</h1><p class="muted">查看真实安装和运行上报。接入暂停只限制 API，不控制现场软件启停。时间：{{ timeZone }}。</p></div></div>
    <p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p>
    <form v-if="!contextual" class="compact-filters" @submit.prevent="list(true)"><label>软件标识<select v-model="softwareId" :disabled="mutation.blocked.value"><option v-for="id in softwareIds" :key="id" :value="id">{{ id }}</option></select></label><label>设备编号<input v-model="deviceNo" maxlength="64" /></label><label>上报 IP<input v-model="reportedIp" /></label><label>上报状态<select v-model="freshness"><option value="">全部</option><option value="NeverReported">尚未上报</option><option value="Fresh">正常上报</option><option value="Unknown">状态未知</option></select></label><label>运行<select v-model="running"><option value="">全部</option><option value="Running">运行</option><option value="Stopped">停止</option><option value="Unknown">未知</option></select></label><label>接入<select v-model="lifecycle"><option value="">全部</option><option value="Active">启用</option><option value="Suspended">暂停</option></select></label><button :disabled="loading || mutation.blocked.value">查询</button></form>
    <div v-if="!contextual" class="panel"><p v-if="!rows.length" class="empty">{{ loading ? '正在读取…' : '没有符合条件的已登记实例。' }}</p><div class="table-wrap" tabindex="0" role="region" aria-label="可横向滚动的数据表格"><table v-if="rows.length"><thead><tr><th>设备</th><th>实际版本</th><th>IP</th><th>运行</th><th>上报状态</th><th>接入</th><th>操作</th></tr></thead><tbody><tr v-for="x in rows" :key="x.id"><td>{{ x.deviceName }}<small>{{ x.deviceNo }}</small></td><td>{{ x.lastSnapshot?.installedVersion ?? installationName(x.lastSnapshot?.installationState) }}</td><td>{{ x.lastSnapshot?.reportedIps.join('、') || '—' }}</td><td>{{ runningName(x.lastSnapshot?.runningState) }}</td><td>{{ freshnessName(x.freshness) }}</td><td>{{ x.lifecycle === 'Active' ? '启用' : '暂停' }}</td><td><button :disabled="loading || mutation.blocked.value" @click="detail(x.id)">查看实例</button></td></tr></tbody></table></div><footer class="pagination"><span>本页 {{ rows.length }} 项</span><div><button :disabled="loading || mutation.blocked.value || cursors.length <= 1" @click="page(false)">上一页</button><button :disabled="loading || mutation.blocked.value || !next" @click="page(true)">下一页</button></div></footer></div>
    <section v-if="selected" class="panel"><div class="section-heading"><div><h2>{{ selected.deviceName }}</h2><p class="muted">{{ selected.deviceNo }} · {{ selected.location.siteName }} / {{ selected.location.processName }}</p></div><button v-if="session.can('instance.manage', selected.softwareId)" :disabled="loading || mutation.blocked.value" @click="open('lifecycle')">{{ selected.lifecycle === 'Active' ? '暂停 API 接入' : '启用 API 接入' }}</button></div>
      <dl class="device-facts"><div><dt>工控机</dt><dd>{{ selected.hardwareName ?? '待补充归属' }}</dd></div><div><dt>管理 IP</dt><dd>{{ selected.managementIp ?? '—' }}</dd></div><div><dt>实例标识</dt><dd>{{ selected.id }}</dd></div><div><dt>上报状态</dt><dd>{{ freshnessName(selected.freshness) }}</dd></div><div><dt>最后接收</dt><dd>{{ selected.lastAcceptedAt ? formatTime(selected.lastAcceptedAt, timeZone) : '尚未上报' }}</dd></div><div><dt>未上报时长</dt><dd>{{ selected.unreportedSeconds === null ? '—' : selected.unreportedSeconds + ' 秒' }}</dd></div><div><dt>实际版本</dt><dd>{{ selected.lastSnapshot?.installedVersion ?? '—' }}</dd></div><div><dt>最新可用正式版</dt><dd><RouterLink v-if="selected.latestAvailableFormalReleaseId" :to="{ path: '/releases', query: { softwareId: selected.softwareId, channel: 'Formal', releaseId: selected.latestAvailableFormalReleaseId } }">查看正式版本</RouterLink><span v-else>暂无</span></dd></div><div><dt>最近任务</dt><dd><RouterLink v-if="selected.latestTaskId" :to="{ path: '/deployments', query: { softwareId:selected.softwareId } }">{{ selected.latestTaskId }} · {{ selected.latestTaskResult ?? '尚无终态' }}</RouterLink><span v-else>暂无</span></dd></div><div><dt>上报 IP</dt><dd>{{ selected.lastSnapshot?.reportedIps.join('、') || '—' }}</dd></div><div><dt>运行状态</dt><dd>{{ runningName(selected.lastSnapshot?.runningState) }}</dd></div><div><dt>本机报告时间</dt><dd>{{ selected.lastSnapshot ? formatTime(selected.lastSnapshot.reportedAt, timeZone) : '—' }}</dd></div></dl><p v-if="selected.freshness === 'Unknown'" class="muted">以上安装和运行值是最后上报的事实，当前状态未知。</p><p v-if="selected.lastSnapshot?.installedVersion && !selected.lastSnapshot.installedReleaseId" class="muted">此现场版本未关联平台发布记录，不能作为测试转正式的证据。</p>
      <h3>设备安装履历</h3><p v-if="!history.length" class="empty">{{ loading ? '正在读取安装履历…' : '尚无有效安装履历。' }}</p><ul class="permission-list"><li v-for="x in history" :key="x.id"><span>{{ installationName(x.installationState) }} · {{ x.installedVersion ?? '—' }}</span><small>接收：{{ formatTime(x.receivedAt, timeZone) }} · 现场安装：{{ x.installedAt ? formatTime(x.installedAt, timeZone) : '未提供' }} · 当时运行：{{ runningName(x.reportedRunningState) }}</small></li></ul><div class="pager-inline"><button :disabled="loading || mutation.blocked.value || historyCursors.length <= 1" @click="subPage('history', false)">上一页履历</button><button :disabled="loading || mutation.blocked.value || !historyNext" @click="subPage('history', true)">下一页履历</button></div>
      <template v-if="session.can('enrollment.manage', selected.softwareId)"><div class="section-heading"><h3>实例凭据</h3><button :disabled="loading || mutation.blocked.value" @click="open('recovery')">签发单次恢复许可</button></div><p class="muted">凭据详情不包含秘密。恢复保留实例及履历，并使旧凭据和旧报告流失效。</p><ul class="permission-list"><li v-for="x in credentials" :key="x.id">{{ x.id }}<small>{{ x.revokedAt ? '已吊销：' + x.revokedAt : '有效' }}</small><button v-if="!x.revokedAt" :disabled="loading || mutation.blocked.value" @click="open('credential', x)">吊销凭据</button></li></ul><div class="pager-inline"><button :disabled="loading || mutation.blocked.value || credentialCursors.length <= 1" @click="subPage('credentials', false)">上一页凭据</button><button :disabled="loading || mutation.blocked.value || !credentialNext" @click="subPage('credentials', true)">下一页凭据</button></div></template>
    </section>
    <section v-if="issued" class="panel"><h2>本次恢复许可</h2><p>操作对象：{{ issuedTarget?.deviceName }} · {{ issuedTarget?.id }}</p><p>{{ issued.id }} · {{ grantStateName(issued.state) }} · {{ issued.expiresAt }}</p><label v-if="secret">一次显示的恢复秘密<input :value="secret" readonly autocomplete="off" /></label><p class="muted">请安全交付给对应实例。关闭后秘密即清除；许可标识与修订应由操作人员记录。</p><button :disabled="mutation.blocked.value" @click="secret = ''">隐藏秘密</button><button v-if="issued.state !== 'Revoked' && issued.state !== 'Consumed'" :disabled="mutation.blocked.value" @click="open('revokeRecovery')">撤销本次恢复许可</button><button :disabled="mutation.blocked.value" @click="close()">完成并清除秘密</button></section>
    <dialog v-if="mode && draft" ref="dialog" @cancel.prevent="close()" aria-labelledby="instance-dialog-title"><h2 id="instance-dialog-title">{{ mode === 'recovery' ? '签发单次恢复许可' : mode === 'credential' ? '吊销实例凭据' : mode === 'revokeRecovery' ? '撤销恢复许可' : '修改 API 接入状态' }}</h2><p class="muted">操作对象：{{ draft.instance.deviceName }} · {{ draft.instance.deviceNo }} · {{ draft.instance.id }}</p><p class="muted">{{ draft.credential ? "凭据：" + draft.credential.id : draft.grant && mode === "revokeRecovery" ? "许可：" + draft.grant.id : mode === "lifecycle" ? "目标状态：" + (draft.action === "Active" ? "启用" : "暂停") + "，修订 " + draft.instance.revision : "单次恢复许可" }}</p><p v-if="!draftValid" class="error" role="alert">人员或权限已变化，请重新核对；未知结果仍须由原人员核实。</p><form @submit.prevent="save"><fieldset :disabled="mutation.blocked.value || !draftValid"><template v-if="mode === 'recovery'"><label>有效期<input v-model="expires" type="datetime-local" required /></label><label>恢复许可秘密<input v-model="secret" type="password" autocomplete="off" required minlength="43" /></label><button type="button" @click="secret = generateSecret()">重新生成秘密</button></template><label>操作原因<textarea v-model="reason" required maxlength="256" /></label></fieldset><p v-if="mutation.error.value" role="alert" class="error">{{ mutation.error.value }}</p><OperationStatus :mutation="mutation" /><p v-if="mutation.uncertain.value" class="muted">结果尚未确认，保留原键，人工核实后再继续。</p><div class="dialog-actions"><button type="button" :disabled="mutation.blocked.value" @click="close(false)">取消</button><button class="primary" :disabled="mutation.busy.value || (!mutation.uncertain.value && !draftValid)">{{ mutation.uncertain.value ? '核实原操作' : '确认提交' }}</button><button v-if="mutation.error.value && !mutation.blocked.value && draft" type="button" @click="detail(draft.instance.id); close(false)">重新加载详情</button></div></form></dialog>
  </section>
</template>
