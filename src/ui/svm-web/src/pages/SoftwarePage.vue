<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, request } from '../api'
import { categoryName, type Page, type Software } from '../catalog'
import { useProtectedMutation } from '../protectedMutation'
import { useSession } from '../session'
const session = useSession(), mutation = useProtectedMutation()
const route = useRoute()
const { busy, uncertain, error, blocked } = mutation
const items = ref<Software[]>([]), selected = ref<Software | null>(null), loading = ref(false), failure = ref(''), notice = ref('')
const nameFilter = ref(''), categoryFilter = ref(''), cursors = ref(['']), nextCursor = ref<string | null>(null)
const mode = ref<'create' | 'edit' | null>(null), dialog = ref<HTMLDialogElement | null>(null)
const code = ref(''), name = ref(''), category = ref<Software['category']>('UpperComputer'), description = ref(''), reason = ref('')
const canEdit = computed(() => selected.value && session.can('release.upload', selected.value.id))
async function failed(e: unknown) {
  failure.value = e instanceof ApiError ? e.message : '软件读取失败。'
  if (e instanceof ApiError && [401, 403, 404].includes(e.status)) await session.load()
}
async function load(reset = false) {
  if (reset) { cursors.value = ['']; selected.value = null }
  loading.value = true; failure.value = ''
  try {
    const query = new URLSearchParams({ pageSize: '50' })
    if (nameFilter.value) query.set('name', nameFilter.value)
    if (categoryFilter.value) query.set('category', categoryFilter.value)
    const cursor = cursors.value[cursors.value.length - 1]; if (cursor) query.set('cursor', cursor)
    const page = await request<Page<Software>>('/api/v1/manage/software?' + query)
    items.value = page.items; nextCursor.value = page.nextCursor
  } catch (e) { items.value = []; nextCursor.value = null; await failed(e) }
  finally { loading.value = false }
}
async function select(id: string) {
  failure.value = ''
  try { selected.value = await request<Software>('/api/v1/manage/software/' + id) }
  catch (e) { selected.value = null; await failed(e) }
}
async function next() { if (nextCursor.value) { cursors.value.push(nextCursor.value); await load() } }
async function previous() { if (cursors.value.length > 1) { cursors.value.pop(); await load() } }
async function open(nextMode: 'create' | 'edit') {
  if (blocked.value) return
  if (nextMode === 'edit' && selected.value) { await select(selected.value.id); if (!selected.value) return }
  mode.value = nextMode; code.value = ''; name.value = nextMode === 'edit' ? selected.value!.name : ''
  description.value = nextMode === 'edit' ? selected.value!.description ?? '' : ''; category.value = 'UpperComputer'; reason.value = ''; error.value = ''
  await nextTick(); dialog.value?.showModal()
}
function close() { if (!blocked.value) { dialog.value?.close(); mode.value = null; error.value = '' } }
async function refresh() {
  if (selected.value && !blocked.value) { await select(selected.value.id); if (selected.value) { name.value = selected.value.name; description.value = selected.value.description ?? ''; reason.value = ''; error.value = '' } }
}
async function save() {
  const creating = mode.value === 'create'
  const body = creating ? { code: code.value, name: name.value, category: category.value, description: description.value || null }
    : { name: name.value, description: description.value || null, reason: reason.value, expectedRevision: selected.value!.revision }
  const result = await mutation.perform<Software>('/api/v1/manage/software' + (creating ? '' : '/' + selected.value!.id), creating ? 'POST' : 'PATCH', body, session.current!.csrfToken)
  if (mutation.completed.value && result) { selected.value = result; dialog.value?.close(); mode.value = null; notice.value = '软件资料已保存。'; await session.load(); if (creating) { cursors.value = ['']; nextCursor.value = null }; await load() }
  else if (!uncertain.value) await session.load()
}
onMounted(async () => { await load(); const id = route.query.id; if (typeof id === 'string' && /^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i.test(id)) await select(id) })
</script>
<template>
  <section class="workspace">
    <div class="page-heading"><div><div class="eyebrow">软件管理</div><h1>软件目录</h1><p class="muted">登记上位机与视觉软件，供设备关联和人员授权使用。</p></div><button v-if="session.can('software.create')" class="primary" :disabled="blocked" @click="open('create')">登记软件</button></div>
    <p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p>
    <div class="account-layout">
      <section class="panel">
        <form class="filters" @submit.prevent="load(true)"><label>软件名称<input v-model="nameFilter" maxlength="128" /></label><label>软件分类<select v-model="categoryFilter" aria-label="软件分类筛选"><option value="">全部分类</option><option value="UpperComputer">上位机</option><option value="Vision">视觉</option></select></label><button :disabled="loading || blocked">查询</button></form>
        <div v-if="loading" class="empty" role="status">正在读取软件…</div><div v-else-if="failure" class="empty"><button @click="load(true)">重新读取</button></div><div v-else-if="!items.length" class="empty">没有可查看的软件。</div>
        <div v-else class="table-scroll"><table><thead><tr><th>软件名称</th><th>软件代码</th><th>分类</th></tr></thead><tbody><tr v-for="item in items" :key="item.id" :class="{ selected: selected?.id === item.id }"><td><button class="text-button" :disabled="blocked" @click="select(item.id)">{{ item.name }}</button></td><td>{{ item.code }}</td><td>{{ categoryName(item.category) }}</td></tr></tbody></table></div>
        <footer class="pagination"><span>本页可查看 {{ items.length }} 项</span><div><button :disabled="loading || blocked || cursors.length <= 1" @click="previous">上一页</button><button :disabled="loading || blocked || !nextCursor" @click="next">下一页</button></div></footer>
      </section>
      <aside class="panel detail"><template v-if="selected"><div class="eyebrow">软件资料</div><h2>{{ selected.name }}</h2><dl><div><dt>软件代码</dt><dd>{{ selected.code }}</dd></div><div><dt>分类</dt><dd>{{ categoryName(selected.category) }}</dd></div><div><dt>资料修订</dt><dd>{{ selected.revision }}</dd></div></dl><p class="description">{{ selected.description || '暂无说明。' }}</p><RouterLink class="text-link" :to="{ path: '/releases', query: { softwareId: selected.id } }">查看版本与安装包</RouterLink><RouterLink v-if="selected.latestAvailableFormalReleaseId" class="text-link" :to="{ path: '/releases', query: { softwareId: selected.id, channel: 'Formal', releaseId: selected.latestAvailableFormalReleaseId } }">查看最新可用正式版</RouterLink><p v-else class="muted">尚无可用正式版本。</p><button v-if="canEdit" :disabled="blocked" @click="open('edit')">修改软件资料</button><p v-else class="muted">修改资料需要此软件的维护权限。</p></template><div v-else class="empty">选择软件，查看资料。</div></aside>
    </div>
    <dialog v-if="mode" ref="dialog" @cancel.prevent="close" aria-labelledby="software-dialog-title"><div class="dialog-heading"><h2 id="software-dialog-title">{{ mode === 'create' ? '登记软件' : '修改软件资料' }}</h2><button :disabled="blocked" aria-label="关闭" @click="close">×</button></div>
      <form @submit.prevent="save"><fieldset :disabled="blocked"><label v-if="mode === 'create'">软件代码<input v-model="code" required maxlength="64" /></label><label>软件名称<input v-model="name" required maxlength="128" /></label><label v-if="mode === 'create'">软件分类<select v-model="category" aria-label="软件分类"><option value="UpperComputer">上位机</option><option value="Vision">视觉</option></select></label><label>软件说明<textarea v-model="description" maxlength="2000" rows="3" /></label><label v-if="mode === 'edit'">操作原因<textarea v-model="reason" required maxlength="256" rows="2" /></label></fieldset>
        <p v-if="mode === 'create'" class="muted">创建后取得此软件的查看、实例查看和映射维护权限。其他操作需由人员管理员授予。</p><p v-if="error" class="error" role="alert">{{ error }}</p><p v-if="uncertain" class="muted">结果待核实。原请求与操作键已保留，请手动核实。</p><div class="dialog-actions"><button v-if="error && !uncertain && mode === 'edit'" type="button" @click="refresh">加载最新详情</button><button v-if="!uncertain" type="button" :disabled="busy" @click="close">取消</button><button class="primary" :disabled="busy">{{ busy ? '正在提交…' : uncertain ? '核实原操作' : '保存' }}</button></div>
      </form>
    </dialog>
  </section>
</template>
