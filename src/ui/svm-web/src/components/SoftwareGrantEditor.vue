<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, request, type Permission } from '../api'
import { operationNames, categoryName, type PermissionOptions } from '../catalog'
const props = defineProps<{ modelValue: Permission[] }>()
const emit = defineEmits<{ 'update:modelValue': [Permission[]] }>()
const options = ref<PermissionOptions | null>(null), loading = ref(false), failure = ref(''), selectedId = ref(''), search = ref('')
const cursors = ref(['']), known = ref<Record<string, string>>({})
const selected = computed(() => options.value?.items.find(s => s.id === selectedId.value))
async function load(reset = false) {
  if (reset) cursors.value = ['']
  loading.value = true; failure.value = ''; selectedId.value = ''
  try {
    const query = new URLSearchParams({ pageSize: '50' }); if (search.value) query.set('name', search.value)
    const cursor = cursors.value[cursors.value.length - 1]; if (cursor) query.set('cursor', cursor)
    options.value = await request<PermissionOptions>('/api/v1/manage/permission-options?' + query)
    for (const item of options.value.items) known.value[item.id] = `${item.name} · ${item.code}`
  } catch (e) { options.value = null; failure.value = e instanceof ApiError ? e.message : '软件授权候选读取失败。' }
  finally { loading.value = false }
}
function change(operation: string, enabled: boolean) {
  if (!selected.value) return
  const remaining = props.modelValue.filter(p => p.softwareId !== selected.value!.id || p.operation !== operation)
  emit('update:modelValue', enabled ? [...remaining, { softwareId: selected.value.id, operation }] : remaining)
}
function remove(permission: Permission) { emit('update:modelValue', props.modelValue.filter(p => p !== permission)) }
async function next() { if (options.value?.nextCursor) { cursors.value.push(options.value.nextCursor); await load() } }
async function previous() { if (cursors.value.length > 1) { cursors.value.pop(); await load() } }
onMounted(() => load())
</script>
<template>
  <section class="software-grants">
    <h3>软件范围授权</h3><p class="muted">按具体软件选择操作。授予权限不会自动执行安装、发布或投放。</p>
    <p v-if="failure" class="error" role="alert">{{ failure }} 既有软件授权仍保留。<button type="button" @click="load()">重试读取</button></p>
    <div class="compact-filters"><label>查找软件<input v-model="search" maxlength="128" /></label><button type="button" :disabled="loading" @click="load(true)">查找软件</button></div>
    <p v-if="loading" class="muted" role="status">正在读取软件候选…</p>
    <template v-if="options">
      <label>选择授权软件<select v-model="selectedId" aria-label="选择授权软件"><option value="">请选择软件</option><option v-for="s in options.items" :key="s.id" :value="s.id">{{ s.name }} · {{ s.code }} · {{ categoryName(s.category) }}</option></select></label>
      <p v-if="!options.items.length" class="muted">没有符合条件的软件。</p>
      <div class="grant-options" v-if="selected"><label v-for="operation in options.softwareOperations" :key="operation" class="check"><input type="checkbox" :checked="modelValue.some(p => p.softwareId === selectedId && p.operation === operation)" @change="change(operation, ($event.target as HTMLInputElement).checked)" />{{ operationNames[operation] ?? operation }}</label></div>
      <div class="pager-inline"><button type="button" :disabled="loading || cursors.length <= 1" @click="previous">上一页软件</button><button type="button" :disabled="loading || !options.nextCursor" @click="next">下一页软件</button></div>
    </template>
    <h3 v-if="modelValue.length">本次软件授权集合</h3><ul class="permission-list"><li v-for="p in modelValue" :key="p.softwareId + p.operation">{{ known[p.softwareId!] ?? p.softwareId }} · {{ operationNames[p.operation] ?? p.operation }}<button v-if="options" type="button" class="text-button" @click="remove(p)">撤销此项</button></li></ul>
  </section>
</template>
