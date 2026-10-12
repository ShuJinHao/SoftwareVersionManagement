<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, request } from '../api'
import { type Page, type Site } from '../catalog'
import { type Instance, type Credential, type History, type Grant, generateSecret, freshnessName, runningName, installationName, grantStateName, formatTime } from '../instances'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
const session = useSession(), route = useRoute(), mutation = useProtectedMutation()
const timeZone = ref('UTC')
const softwareId = ref(String(route.query.softwareId ?? '')), deviceNo = ref(''), reportedIp = ref(''), freshness = ref(''), running = ref(''), lifecycle = ref('')
const filters = ref<Record<string, string>>({}), rows = ref<Instance[]>([]), selected = ref<Instance | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const cursors = ref<string[]>(['']), next = ref<string | null>(null), history = ref<History[]>([]), historyCursors = ref<string[]>(['']), historyNext = ref<string | null>(null)
const credentials = ref<Credential[]>([]), credentialCursors = ref<string[]>(['']), credentialNext = ref<string | null>(null)
const mode = ref<'lifecycle' | 'credential' | 'recovery' | 'revokeRecovery' | null>(null), credential = ref<Credential | null>(null), reason = ref(''), secret = ref(''), expires = ref(''), issued = ref<Grant | null>(null)
const softwareIds = computed(() => session.current?.permissions.filter(p => p.operation === 'instance.read' && p.softwareId).map(p => p.softwareId!) ?? [])
async function fail(e: unknown) { failure.value = e instanceof ApiError ? e.message : '读取失败，请重试。'; await session.load() }
async function list(reset = false) {
  if (mutation.blocked.value) return
  if (reset) { filters.value = Object.fromEntries(Object.entries({ softwareId: softwareId.value, deviceNo: deviceNo.value, reportedIp: reportedIp.value, freshness: freshness.value, runningState: running.value, lifecycle: lifecycle.value }).filter(([, v]) => v)); cursors.value = ['']; selected.value = null }
  if (!filters.value.softwareId) return
  loading.value = true; failure.value = ''
  try { const q = new URLSearchParams({ ...filters.value, pageSize: '50' }); if (cursors.value[cursors.value.length - 1]) q.set('cursor', cursors.value[cursors.value.length - 1]!); const p = await request<Page<Instance>>('/api/v1/manage/instances?' + q); rows.value = p.items; next.value = p.nextCursor }
  catch (e) { rows.value = []; next.value = null; await fail(e) } finally { loading.value = false }
}
async function page(forward: boolean) { if (forward && next.value) cursors.value.push(next.value); else if (!forward && cursors.value.length > 1) cursors.value.pop(); await list() }
async function loadHistory() {
  if (!selected.value) return
  const q = new URLSearchParams({ pageSize: '50' }); if (historyCursors.value[historyCursors.value.length - 1]) q.set('cursor', historyCursors.value[historyCursors.value.length - 1]!)
  const p = await request<Page<History>>(`/api/v1/manage/instances/${selected.value.id}/version-history?` + q); history.value = p.items; historyNext.value = p.nextCursor
}
async function loadCredentials() {
  if (!selected.value || !session.can('enrollment.manage', selected.value.softwareId)) return
  const q = new URLSearchParams({ pageSize: '50' }); if (credentialCursors.value[credentialCursors.value.length - 1]) q.set('cursor', credentialCursors.value[credentialCursors.value.length - 1]!)
  const p = await request<Page<Credential>>(`/api/v1/manage/instances/${selected.value.id}/credentials?` + q); credentials.value = p.items; credentialNext.value = p.nextCursor
}
async function detail(id: string) {
  if (mutation.blocked.value) return
  loading.value = true; failure.value = ''; selected.value = null; history.value = []; credentials.value = []; historyCursors.value = ['']; credentialCursors.value = ['']
  try { selected.value = await request<Instance>('/api/v1/manage/instances/' + id); await loadHistory(); await loadCredentials() } catch (e) { await fail(e) } finally { loading.value = false }
}
async function subPage(kind: 'history' | 'credentials', forward: boolean) {
  if (loading.value || mutation.blocked.value) return
  const stack = kind === 'history' ? historyCursors : credentialCursors, cursor = kind === 'history' ? historyNext : credentialNext
  if (forward && cursor.value) stack.value.push(cursor.value); else if (!forward && stack.value.length > 1) stack.value.pop()
  loading.value = true; try { if (kind === 'history') await loadHistory(); else await loadCredentials() } catch (e) { await fail(e) } finally { loading.value = false }
}
function open(value: typeof mode.value, c?: Credential) { mode.value = value; credential.value = c ?? null; reason.value = ''; secret.value = value === 'recovery' ? generateSecret() : ''; expires.value = ''; mutation.error.value = '' }
function close() { if (mutation.blocked.value) return; mode.value = null; secret.value = ''; issued.value = null }
async function save() {
  if (!selected.value) return
  const id = selected.value.id; let path = '', method = 'POST', body: object
  if (mode.value === 'lifecycle') { path = `/api/v1/manage/instances/${id}/lifecycle`; method = 'PATCH'; body = { lifecycle: selected.value.lifecycle === 'Active' ? 'Suspended' : 'Active', expectedRevision: selected.value.revision, reason: reason.value } }
  else if (mode.value === 'credential') { path = `/api/v1/manage/credentials/${credential.value!.id}/revoke`; body = { expectedRevision: credential.value!.revision, reason: reason.value } }
  else if (mode.value === 'revokeRecovery') { path = `/api/v1/manage/recovery-grants/${issued.value!.id}/revoke`; body = { expectedRevision: issued.value!.revision, reason: reason.value } }
  else { path = `/api/v1/manage/instances/${id}/recovery-grants`; body = { expiresAt: new Date(expires.value).toISOString(), secretMaterial: secret.value, reason: reason.value } }
  const currentMode = mode.value, result = await mutation.perform<Grant | Instance | Credential>(path, method, body, session.current!.csrfToken)
  if (mutation.completed.value && result) {
    notice.value = '接入管理操作已完成。'; await session.load()
    if (currentMode === 'recovery') { issued.value = result as Grant; mode.value = null }
    else { close(); await detail(id) }
  } else if (!mutation.uncertain.value) { secret.value = ''; await session.load() }
}
onMounted(async () => {
  if (session.canAssets) {
    try { timeZone.value = (await request<Site>('/api/v1/manage/site')).siteTimeZone }
    catch (e) { if (e instanceof ApiError && (e.status === 401 || e.status === 403)) await session.load() }
  }
  if (!softwareId.value) softwareId.value = softwareIds.value[0] ?? ''; await list(true); if (route.query.id) await detail(String(route.query.id)) })
onUnmounted(() => { secret.value = '' })
</script>
<template>
  <section class="workspace"><div class="page-heading"><div><div class="eyebrow">现场软件</div><h1>软件实例</h1><p class="muted">查看真实安装和运行上报。接入暂停只限制 API，不控制现场软件启停。时间：{{ timeZone }}。</p></div></div>
    <p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p>
    <form class="compact-filters" @submit.prevent="list(true)"><label>软件标识<select v-model="softwareId" :disabled="mutation.blocked.value"><option v-for="id in softwareIds" :key="id" :value="id">{{ id }}</option></select></label><label>设备编号<input v-model="deviceNo" maxlength="64" /></label><label>上报 IP<input v-model="reportedIp" /></label><label>上报状态<select v-model="freshness"><option value="">全部</option><option value="NeverReported">尚未上报</option><option value="Fresh">正常上报</option><option value="Unknown">状态未知</option></select></label><label>运行<select v-model="running"><option value="">全部</option><option value="Running">运行</option><option value="Stopped">停止</option><option value="Unknown">未知</option></select></label><label>接入<select v-model="lifecycle"><option value="">全部</option><option value="Active">启用</option><option value="Suspended">暂停</option></select></label><button :disabled="loading || mutation.blocked.value">查询</button></form>
    <div class="panel"><p v-if="!rows.length" class="empty">{{ loading ? '正在读取…' : '没有符合条件的已登记实例。' }}</p><div class="table-wrap"><table v-if="rows.length"><thead><tr><th>设备</th><th>实际版本</th><th>IP</th><th>运行</th><th>上报状态</th><th>接入</th><th>操作</th></tr></thead><tbody><tr v-for="x in rows" :key="x.id"><td>{{ x.deviceName }}<small>{{ x.deviceNo }}</small></td><td>{{ x.lastSnapshot?.installedVersion ?? installationName(x.lastSnapshot?.installationState) }}</td><td>{{ x.lastSnapshot?.reportedIps.join('、') || '—' }}</td><td>{{ runningName(x.lastSnapshot?.runningState) }}</td><td>{{ freshnessName(x.freshness) }}</td><td>{{ x.lifecycle === 'Active' ? '启用' : '暂停' }}</td><td><button :disabled="loading || mutation.blocked.value" @click="detail(x.id)">查看实例</button></td></tr></tbody></table></div><footer class="pagination"><span>本页 {{ rows.length }} 项</span><div><button :disabled="loading || mutation.blocked.value || cursors.length <= 1" @click="page(false)">上一页</button><button :disabled="loading || mutation.blocked.value || !next" @click="page(true)">下一页</button></div></footer></div>
    <section v-if="selected" class="panel"><div class="section-heading"><div><h2>{{ selected.deviceName }}</h2><p class="muted">{{ selected.deviceNo }} · {{ selected.location.siteName }} / {{ selected.location.processName }}</p></div><button v-if="session.can('instance.manage', selected.softwareId)" :disabled="loading || mutation.blocked.value" @click="open('lifecycle')">{{ selected.lifecycle === 'Active' ? '暂停 API 接入' : '启用 API 接入' }}</button></div>
      <dl class="device-facts"><div><dt>实例标识</dt><dd>{{ selected.id }}</dd></div><div><dt>上报状态</dt><dd>{{ freshnessName(selected.freshness) }}</dd></div><div><dt>最后接收</dt><dd>{{ selected.lastAcceptedAt ? formatTime(selected.lastAcceptedAt, timeZone) : '尚未上报' }}</dd></div><div><dt>未上报时长</dt><dd>{{ selected.unreportedSeconds === null ? '—' : selected.unreportedSeconds + ' 秒' }}</dd></div><div><dt>实际版本</dt><dd>{{ selected.lastSnapshot?.installedVersion ?? '—' }}</dd></div><div><dt>最新可用正式版</dt><dd><RouterLink v-if="selected.latestAvailableFormalReleaseId" :to="{ path: '/releases', query: { softwareId: selected.softwareId, channel: 'Formal', releaseId: selected.latestAvailableFormalReleaseId } }">查看正式版本</RouterLink><span v-else>暂无</span></dd></div><div><dt>上报 IP</dt><dd>{{ selected.lastSnapshot?.reportedIps.join('、') || '—' }}</dd></div><div><dt>运行状态</dt><dd>{{ runningName(selected.lastSnapshot?.runningState) }}</dd></div><div><dt>本机报告时间</dt><dd>{{ selected.lastSnapshot ? formatTime(selected.lastSnapshot.reportedAt, timeZone) : '—' }}</dd></div></dl><p v-if="selected.freshness === 'Unknown'" class="muted">以上安装和运行值是最后上报的事实，当前状态未知。</p><p v-if="selected.lastSnapshot?.installedVersion && !selected.lastSnapshot.installedReleaseId" class="muted">此现场版本未关联平台发布记录，不能作为测试转正式的证据。</p>
      <h3>设备安装履历</h3><p v-if="!history.length" class="empty">{{ loading ? '正在读取安装履历…' : '尚无有效安装履历。' }}</p><ul class="permission-list"><li v-for="x in history" :key="x.id"><span>{{ installationName(x.installationState) }} · {{ x.installedVersion ?? '—' }}</span><small>接收：{{ formatTime(x.receivedAt, timeZone) }} · 现场安装：{{ x.installedAt ? formatTime(x.installedAt, timeZone) : '未提供' }} · 当时运行：{{ runningName(x.reportedRunningState) }}</small></li></ul><div class="pager-inline"><button :disabled="loading || mutation.blocked.value || historyCursors.length <= 1" @click="subPage('history', false)">上一页履历</button><button :disabled="loading || mutation.blocked.value || !historyNext" @click="subPage('history', true)">下一页履历</button></div>
      <template v-if="session.can('enrollment.manage', selected.softwareId)"><div class="section-heading"><h3>实例凭据</h3><button :disabled="loading || mutation.blocked.value" @click="open('recovery')">签发单次恢复许可</button></div><p class="muted">凭据详情不包含秘密。恢复保留实例及履历，并使旧凭据和旧报告流失效。</p><ul class="permission-list"><li v-for="x in credentials" :key="x.id">{{ x.id }}<small>{{ x.revokedAt ? '已吊销：' + x.revokedAt : '有效' }}</small><button v-if="!x.revokedAt" :disabled="loading || mutation.blocked.value" @click="open('credential', x)">吊销凭据</button></li></ul><div class="pager-inline"><button :disabled="loading || mutation.blocked.value || credentialCursors.length <= 1" @click="subPage('credentials', false)">上一页凭据</button><button :disabled="loading || mutation.blocked.value || !credentialNext" @click="subPage('credentials', true)">下一页凭据</button></div></template>
    </section>
    <section v-if="issued" class="panel"><h2>本次恢复许可</h2><p>{{ issued.id }} · {{ grantStateName(issued.state) }} · {{ issued.expiresAt }}</p><label v-if="secret">一次显示的恢复秘密<input :value="secret" readonly autocomplete="off" /></label><p class="muted">请安全交付给对应实例。关闭后秘密即清除；许可标识与修订应由操作人员记录。</p><button :disabled="mutation.blocked.value" @click="secret = ''">隐藏秘密</button><button v-if="issued.state !== 'Revoked' && issued.state !== 'Consumed'" :disabled="mutation.blocked.value" @click="open('revokeRecovery')">撤销本次恢复许可</button><button :disabled="mutation.blocked.value" @click="close">完成并清除秘密</button></section>
    <section v-if="mode" class="panel"><h2>{{ mode === 'recovery' ? '签发单次恢复许可' : mode === 'credential' ? '吊销实例凭据' : mode === 'revokeRecovery' ? '撤销恢复许可' : '修改 API 接入状态' }}</h2><form @submit.prevent="save"><fieldset :disabled="mutation.blocked.value"><template v-if="mode === 'recovery'"><label>有效期<input v-model="expires" type="datetime-local" required /></label><label>恢复许可秘密<input v-model="secret" type="password" autocomplete="off" required minlength="43" /></label><button type="button" @click="secret = generateSecret()">重新生成秘密</button></template><label>操作原因<textarea v-model="reason" required maxlength="256" /></label></fieldset><p v-if="mutation.error.value" role="alert" class="error">{{ mutation.error.value }}</p><p v-if="mutation.uncertain.value" class="muted">结果尚未确认，保留原键，人工核实后再继续。</p><div class="dialog-actions"><button type="button" :disabled="mutation.blocked.value" @click="close">取消</button><button class="primary" :disabled="mutation.busy.value">{{ mutation.uncertain.value ? '核实原操作' : '确认提交' }}</button><button v-if="mutation.error.value && !mutation.blocked.value && selected" type="button" @click="close(); detail(selected.id)">重新加载详情</button></div></form></section>
  </section>
</template>
