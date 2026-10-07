<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { ApiError, request, type User, type UserPage, type Permission } from '../api'
import { useSession } from '../session'
import { pendingWriteKey, useMutation } from '../mutation'
import SoftwareGrantEditor from '../components/SoftwareGrantEditor.vue'
const softwareGrants = ref<Permission[]>([])
const session = useSession()
const users = ref<User[]>([]), selected = ref<User | null>(null), loading = ref(false), detailLoading = ref(false), readError = ref(''), notice = ref('')
const employeeFilter = ref(''), enabledFilter = ref(''), cursors = ref(['']), nextCursor = ref<string | null>(null), readAt = ref('')
const mode = ref<'create' | 'edit' | 'permissions' | 'reset' | null>(null), dialog = ref<HTMLDialogElement | null>(null)
const employeeNo = ref(''), displayName = ref(''), temporaryPassword = ref(''), isEnabled = ref(true), reason = ref(''), grants = ref<string[]>([])
const labels: Record<string, string> = { 'identity.manage': '管理人员与身份', 'software.create': '登记软件', 'asset.read': '查看现场台账', 'asset.manage': '维护现场台账' }
const title = computed(() => ({ create: '创建人员账号', edit: '修改账号资料', permissions: '维护人员授权', reset: '重置账号密码' }[mode.value ?? 'create']))
const mutation = useMutation(), { perform, busy, uncertain, error } = mutation
const pendingWrite = inject(pendingWriteKey, ref(false))
watch([busy, uncertain], () => { pendingWrite.value = busy.value || uncertain.value }, { flush: 'sync' })
async function load() {
  loading.value = true; readError.value = ''
  try {
    const query = new URLSearchParams({ pageSize: '50' })
    if (employeeFilter.value) query.set('employeeNo', employeeFilter.value)
    if (enabledFilter.value) query.set('isEnabled', enabledFilter.value)
    const lastCursor = cursors.value[cursors.value.length - 1]
    if (lastCursor) query.set('cursor', lastCursor)
    const page = await request<UserPage>('/api/v1/manage/users?' + query)
    users.value = page.items; nextCursor.value = page.nextCursor; readAt.value = page.serverTime
  } catch (e) {
    users.value = []; nextCursor.value = null; readError.value = e instanceof ApiError ? e.message : '账号列表读取失败。'
    if (e instanceof ApiError && (e.status === 401 || e.status === 403)) await session.load()
  } finally { loading.value = false }
}
async function select(id: string) {
  if (detailLoading.value) return
  detailLoading.value = true; readError.value = ''
  try { selected.value = await request<User>('/api/v1/manage/users/' + id) }
  catch (e) {
    selected.value = null; readError.value = e instanceof ApiError ? e.message : '账号详情读取失败。'
    if (e instanceof ApiError && (e.status === 401 || e.status === 403)) await session.load()
  }
  finally { detailLoading.value = false }
}
async function filter() { cursors.value = ['']; selected.value = null; await load() }
async function next() { if (nextCursor.value) { cursors.value.push(nextCursor.value); await load() } }
async function previous() { if (cursors.value.length > 1) { cursors.value.pop(); await load() } }
async function open(nextMode: NonNullable<typeof mode.value>) {
  if (busy.value || uncertain.value) return
  if (nextMode !== 'create' && selected.value) { await select(selected.value.id); if (!selected.value) return }
  employeeNo.value = ''; displayName.value = selected.value?.displayName ?? ''; temporaryPassword.value = ''; reason.value = ''
  isEnabled.value = selected.value?.isEnabled ?? true
  grants.value = selected.value?.permissions.filter(p => p.softwareId === null).map(p => p.operation) ?? []
  softwareGrants.value = selected.value?.permissions.filter(p => p.softwareId !== null).map(p => ({ ...p })) ?? []
  error.value = ''; mode.value = nextMode
  await nextTick(); dialog.value?.showModal()
}
function close() {
  if (busy.value || uncertain.value) return
  dialog.value?.close(); mode.value = null; temporaryPassword.value = ''; error.value = ''
}
async function refreshDraft() {
  if (!selected.value || busy.value || uncertain.value) return
  await select(selected.value.id)
  if (!selected.value) return
  softwareGrants.value = selected.value.permissions.filter(p => p.softwareId !== null).map(p => ({ ...p }))
  displayName.value = selected.value.displayName; isEnabled.value = selected.value.isEnabled
  grants.value = selected.value.permissions.filter(p => p.softwareId === null).map(p => p.operation)
  temporaryPassword.value = ''; reason.value = ''; error.value = ''
}
async function save() {
  let path = '/api/v1/manage/users', method = 'POST', body: object
  if (mode.value === 'create') body = { employeeNo: employeeNo.value, displayName: displayName.value, temporaryPassword: temporaryPassword.value }
  else {
    if (!selected.value) return
    const common = { expectedRevision: selected.value.revision, reason: reason.value }
    if (mode.value === 'edit') { method = 'PATCH'; path += '/' + selected.value.id; body = { ...common, displayName: displayName.value, isEnabled: isEnabled.value } }
    else if (mode.value === 'reset') { path += '/' + selected.value.id + '/reset-password'; body = { ...common, temporaryPassword: temporaryPassword.value } }
    else { method = 'PUT'; path = '/api/v1/manage/subjects/' + selected.value.id + '/permissions'; body = { ...common,
      permissions: [...grants.value.map(operation => ({ softwareId: null, operation })), ...softwareGrants.value] } }
  }
  const result = await perform<User>(path, method, body, session.current!.csrfToken)
  if (result) {
    selected.value = result; temporaryPassword.value = ''; dialog.value?.close(); mode.value = null
    notice.value = '操作已完成。'; await session.load()
    if (session.canManage) await load()
  } else if (!uncertain.value) {
    temporaryPassword.value = ''
    // Re-read only the session so permission revocation is reflected; never retry the write.
    await session.load()
  }
}
onBeforeRouteLeave(() => {
  if (busy.value || uncertain.value) { error.value = '请先等待或核实原操作，再离开此页面。'; return false }
})
function beforeUnload(event: BeforeUnloadEvent) {
  if (busy.value || uncertain.value) { event.preventDefault(); event.returnValue = '' }
}
onMounted(() => { window.addEventListener('beforeunload', beforeUnload); void load() })
onUnmounted(() => { window.removeEventListener('beforeunload', beforeUnload); pendingWrite.value = false; temporaryPassword.value = ''; mutation.dispose() })
</script>
<template>
  <section class="workspace">
    <div class="page-heading"><div><div class="eyebrow">平台管理</div><h1>人员账号</h1><p class="muted">管理本厂人员的访问权限和账号状态。</p></div><button class="primary" @click="open('create')">创建账号</button></div>
    <p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="readError" class="error" role="alert">{{ readError }}</p>
    <div class="account-layout">
      <section class="panel">
        <form class="filters" @submit.prevent="filter">
          <label>工号筛选<input v-model="employeeFilter" placeholder="按工号前缀查找" maxlength="64" :disabled="loading" /></label>
          <label>账号状态<select v-model="enabledFilter" :disabled="loading"><option value="">全部状态</option><option value="true">启用</option><option value="false">停用</option></select></label>
          <button type="submit" :disabled="loading">查询</button>
        </form>
        <div v-if="loading" class="empty" role="status">正在读取账号…</div>
        <div v-else-if="readError && !users.length" class="empty"><p>账号列表暂不可用</p><button @click="load">重试读取</button></div>
        <div v-else-if="!users.length" class="empty">没有符合条件的账号。</div>
        <div v-else class="table-scroll"><table><thead><tr><th>人员</th><th>工号</th><th>账号状态</th><th>密码状态</th></tr></thead><tbody>
          <tr v-for="user in users" :key="user.id" :class="{ selected: selected?.id === user.id }"><td><button class="text-button" :disabled="detailLoading" @click="select(user.id)">{{ user.displayName }}</button></td><td class="mono">{{ user.employeeNo }}</td><td><span :class="['badge', user.isEnabled ? 'enabled' : 'disabled']">{{ user.isEnabled ? '启用' : '停用' }}</span></td><td>{{ user.mustChangePassword ? '待首次改密' : '已设置' }}</td></tr>
        </tbody></table></div>
        <footer class="pagination"><span>本页 {{ users.length }} 人<span v-if="readAt" class="read-time"> · 读取于 {{ new Date(readAt).toLocaleTimeString() }}</span></span><div><button :disabled="loading || cursors.length <= 1" @click="previous">上一页</button><button :disabled="loading || !nextCursor" @click="next">下一页</button></div></footer>
      </section>
      <aside class="panel detail">
        <div v-if="detailLoading" class="empty" role="status">正在读取详情…</div>
        <template v-else-if="selected">
          <div class="eyebrow">账号详情</div><h2>{{ selected.displayName }}</h2><p class="muted mono">{{ selected.employeeNo }}</p>
          <dl><div><dt>账号状态</dt><dd>{{ selected.isEnabled ? '启用' : '停用' }}</dd></div><div><dt>密码状态</dt><dd>{{ selected.mustChangePassword ? '下次登录须改密' : '已设置' }}</dd></div><div><dt>资料修订</dt><dd>{{ selected.revision }}</dd></div></dl>
          <h3>已授予权限</h3><p v-if="!selected.permissions.length" class="muted">尚未授予业务权限。</p><ul class="permission-list"><li v-for="p in selected.permissions" :key="p.operation + p.softwareId">{{ labels[p.operation] ?? p.operation }}<small v-if="p.softwareId">软件 {{ p.softwareId }}</small></li></ul>
          <div class="detail-actions"><button @click="open('edit')">修改资料 / 启停</button><button @click="open('permissions')">维护厂级权限与软件授权</button><button @click="open('reset')">重置密码</button><button class="text-button" @click="select(selected.id)">重新加载详情</button></div>
        </template><div v-else class="empty">选择人员，查看账号资料与权限。</div>
      </aside>
    </div>
    <dialog v-if="mode" ref="dialog" @cancel.prevent="close" aria-labelledby="account-dialog-title">
      <div class="dialog-heading"><h2 id="account-dialog-title">{{ title }}</h2><button :disabled="busy || uncertain" @click="close" aria-label="关闭">×</button></div>
      <p v-if="mode !== 'create'" class="muted">{{ selected?.displayName }} · {{ selected?.employeeNo }}</p>
      <form @submit.prevent="save">
        <fieldset :disabled="busy || uncertain">
          <label v-if="mode === 'create'">工号<input v-model="employeeNo" required maxlength="64" pattern="[A-Za-z0-9._-]+" autocomplete="off" /></label>
          <label v-if="mode === 'create' || mode === 'edit'">显示名称<input v-model="displayName" required maxlength="128" /></label>
          <label v-if="mode === 'edit'" class="check"><input v-model="isEnabled" type="checkbox" />启用账号</label>
          <template v-if="mode === 'create' || mode === 'reset'"><label>临时密码<input v-model="temporaryPassword" type="password" required minlength="15" maxlength="128" autocomplete="new-password" /></label><p class="muted">请通过受控方式交给本人。下次登录须改密；重置密码会撤销旧会话。</p></template>
          <template v-if="mode === 'permissions'"><div class="grant-options"><label v-for="(label, operation) in labels" :key="operation" class="check"><input v-model="grants" :value="operation" type="checkbox" />{{ label }}</label></div><SoftwareGrantEditor v-model="softwareGrants" /></template>
          <label v-if="mode !== 'create'">操作原因<textarea v-model="reason" required maxlength="256" rows="3"></textarea></label>
        </fieldset>
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <p v-if="uncertain" class="muted">原请求已保留，结果仍待核实。请保持此页，手动核实原操作。</p>
        <div class="dialog-actions"><button v-if="!uncertain" type="button" :disabled="busy" @click="close">取消</button><button v-if="error && !uncertain && selected && mode !== 'create'" type="button" @click="refreshDraft">加载最新详情</button><button class="primary" type="submit" :disabled="busy">{{ busy ? '正在提交…' : uncertain ? '核实原操作' : '保存' }}</button></div>
      </form>
    </dialog>
  </section>
</template>
