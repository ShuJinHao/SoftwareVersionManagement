<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, request } from '../api'
import { type Page } from '../catalog'
import { type Grant, generateSecret, grantStateName } from '../instances'
import { useSession } from '../session'
import { useProtectedMutation } from '../protectedMutation'
const session = useSession(), route = useRoute(), mutation = useProtectedMutation()
const softwareId = ref(String(route.query.softwareId ?? '')), appliedSoftware = ref(''), rows = ref<Grant[]>([]), loading = ref(false), failure = ref(''), notice = ref('')
const cursors = ref<string[]>(['']), next = ref<string | null>(null), form = ref(false), secret = ref(''), deviceIds = ref(String(route.query.deviceId ?? ''))
const expires = ref(''), capacity = ref(1), reason = ref(''), target = ref<Grant | null>(null), issued = ref<Grant | null>(null)
const softwareIds = computed(() => session.current?.permissions.filter(p => p.operation === 'enrollment.manage' && p.softwareId).map(p => p.softwareId!) ?? [])
async function load(reset = false) {
  if (mutation.blocked.value) return
  if (reset) { appliedSoftware.value = softwareId.value; cursors.value = ['']; rows.value = [] }
  if (!appliedSoftware.value) return
  loading.value = true; failure.value = ''
  try { const q = new URLSearchParams({ softwareId: appliedSoftware.value, pageSize: '50' }); if (cursors.value[cursors.value.length - 1]) q.set('cursor', cursors.value[cursors.value.length - 1]!); const page = await request<Page<Grant>>('/api/v1/manage/enrollment-grants?' + q); rows.value = page.items; next.value = page.nextCursor }
  catch (e) { rows.value = []; next.value = null; failure.value = e instanceof ApiError ? e.message : '许可读取失败。'; await session.load() }
  finally { loading.value = false }
}
async function page(forward: boolean) { if (forward && next.value) cursors.value.push(next.value); else if (!forward && cursors.value.length > 1) cursors.value.pop(); await load() }
function create() { if (form.value || mutation.blocked.value) return; target.value = null; form.value = true; issued.value = null; secret.value = generateSecret(); expires.value = ''; reason.value = ''; capacity.value = 1; mutation.error.value = '' }
function revoke(x: Grant) { if (form.value || mutation.blocked.value) return; target.value = x; form.value = true; secret.value = ''; reason.value = ''; issued.value = null; mutation.error.value = '' }
function close() { if (mutation.blocked.value) return; form.value = false; secret.value = ''; issued.value = null; target.value = null }
async function save() {
  const x = target.value
  const path = x ? `/api/v1/manage/enrollment-grants/${x.id}/revoke` : '/api/v1/manage/enrollment-grants'
  const body = x ? { expectedRevision: x.revision, reason: reason.value } : { softwareId: appliedSoftware.value, deviceIds: deviceIds.value.split(/[\s,，]+/).filter(Boolean), expiresAt: new Date(expires.value).toISOString(), maxInstances: capacity.value, secretMaterial: secret.value, reason: reason.value }
  const result = await mutation.perform<Grant>(path, 'POST', body, session.current!.csrfToken)
  if (mutation.completed.value && result) { notice.value = x ? '登记许可已撤销，已有实例身份继续保留。' : '登记许可已创建，请安全交付授权标识和秘密。'; if (x) close(); else issued.value = result; await session.load(); await load() }
  else if (!mutation.uncertain.value) { secret.value = ''; await session.load() }
}
onMounted(async () => { if (!softwareId.value) softwareId.value = softwareIds.value[0] ?? ''; await load(true) })
onUnmounted(() => { secret.value = '' })
</script>
<template>
  <section class="workspace"><div class="page-heading"><div><div class="eyebrow">现场接入</div><h1>登记许可</h1><p class="muted">限定软件、已有设备映射、有效期和登记名额。撤销许可不会吊销已登记的实例凭据。</p></div><button class="primary" :disabled="!appliedSoftware || loading || form || mutation.blocked.value" @click="create">签发登记许可</button></div>
    <p v-if="notice" role="status" class="success">{{ notice }}</p><p v-if="failure" role="alert" class="error">{{ failure }}</p>
    <form class="compact-filters" @submit.prevent="load(true)"><label>软件标识<select v-model="softwareId" :disabled="form || mutation.blocked.value"><option v-for="id in softwareIds" :key="id" :value="id">{{ id }}</option></select></label><button :disabled="loading || form || mutation.blocked.value">读取许可</button></form>
    <div class="panel"><p v-if="!rows.length" class="empty">{{ loading ? '正在读取…' : '本页没有登记许可。' }}</p><div class="table-wrap"><table v-if="rows.length"><thead><tr><th>授权标识</th><th>设备范围</th><th>名额</th><th>有效期</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="x in rows" :key="x.id"><td>{{ x.id }}</td><td>{{ x.deviceIds?.join('、') }}</td><td>{{ x.usedCount }} / {{ x.maxInstances }}</td><td>{{ x.expiresAt }}</td><td>{{ grantStateName(x.state) }}</td><td><button v-if="x.state !== 'Revoked'" :disabled="loading || form || mutation.blocked.value" @click="revoke(x)">撤销</button></td></tr></tbody></table></div><footer class="pagination"><span>本页 {{ rows.length }} 项</span><div><button :disabled="loading || mutation.blocked.value || cursors.length <= 1" @click="page(false)">上一页</button><button :disabled="loading || mutation.blocked.value || !next" @click="page(true)">下一页</button></div></footer></div>
    <section v-if="form" class="panel"><h2>{{ target ? '撤销登记许可' : '签发登记许可' }}</h2><form @submit.prevent="save"><fieldset :disabled="mutation.blocked.value || !!issued">
      <template v-if="!target"><label>平台已映射设备标识<textarea v-model="deviceIds" required rows="2" placeholder="从现场台账取得设备 UUID，以逗号或空格分隔" /></label><label>有效期<input v-model="expires" type="datetime-local" required /></label><label>最大登记数量<input v-model.number="capacity" type="number" min="1" max="100000" required /></label><label>登记许可秘密<input v-model="secret" type="password" autocomplete="off" required minlength="43" /></label><button type="button" @click="secret = generateSecret()">重新生成秘密</button></template>
      <label>操作原因<textarea v-model="reason" maxlength="256" required /></label></fieldset>
      <template v-if="issued"><p role="status">授权标识：{{ issued.id }}</p><label>本次秘密（关闭后不再显示）<input :value="secret" readonly autocomplete="off" /></label><p class="muted">秘密仅留在当前表单内存；请通过受控渠道交付厂家。接口和数据库不保存明文。</p></template>
      <p v-if="mutation.error.value" class="error" role="alert">{{ mutation.error.value }}</p><p v-if="mutation.uncertain.value" class="muted">结果尚未确认，保留原操作键与表单；仅通过下方按钮人工核实。</p><div class="dialog-actions"><button type="button" :disabled="mutation.blocked.value" @click="close">{{ issued ? '完成并清除秘密' : '取消' }}</button><button v-if="!issued" class="primary" :disabled="mutation.busy.value">{{ mutation.uncertain.value ? '核实原操作' : target ? '确认撤销' : '创建许可' }}</button></div>
    </form></section>
  </section>
</template>
