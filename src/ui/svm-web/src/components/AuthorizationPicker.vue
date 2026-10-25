<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { request } from '../api'
import { useOwnedReads } from '../ownedReads'
import type { Role } from '../authorization'
import type { Device, Process, Software } from '../catalog'
const props=defineProps<{kind:'Role'|'Process'|'Device'|'Software'; caption:string; disabled?:boolean}>()
const emit=defineEmits<{choose:[{id:string;name:string;role?:Role}]}>()
interface Options {items:Software[]; roles?:Role[]; processes?:Process[]; devices?:Device[]; nextCursor:string|null}
const input=ref(''),applied=ref(''),next=ref<string|null>(null),choices=ref<{id:string;name:string;role?:Role}[]>([]),selected=ref(''),error=ref('')
const reads=useOwnedReads(()=>{choices.value=[];next.value=null;selected.value=''})
async function load(cursor?:string|null){const kind=props.kind,search=cursor?applied.value:input.value.trim();error.value=''
  const q=new URLSearchParams({kind,pageSize:'50'});if(search)q.set('name',search);if(cursor)q.set('cursor',cursor)
  await reads.run('candidates',!cursor,s=>request<Options>('/api/v1/manage/permission-options?'+q,'GET',undefined,undefined,undefined,{signal:s}),p=>{
    const items=kind==='Role'?(p.roles??[]).map(r=>({id:r.id,name:r.name+(r.isEnabled?'':'（已停用）'),role:r})):kind==='Process'?(p.processes??[]).map(x=>({id:x.id,name:x.name+' · '+x.code})):kind==='Device'?(p.devices??[]).map(x=>({id:x.id,name:x.processName+' / '+x.name+' · '+x.deviceNo})):p.items.map(x=>({id:x.id,name:x.name+' · '+x.code}))
    choices.value=cursor?[...choices.value,...items.filter(x=>!choices.value.some(v=>v.id===x.id))]:items;applied.value=search;next.value=p.nextCursor;selected.value=''
  },e=>{error.value=e instanceof Error?e.message:'候选读取失败'})}
function choose(){const value=choices.value.find(x=>x.id===selected.value);if(value && !props.disabled)emit('choose',value)}
watch(()=>props.kind,()=>{choices.value=[];input.value='';void load()})
onMounted(()=>load())
</script>
<template><div class="authorization-picker"><label>{{ caption }}<div class="compact-filters"><input v-model="input" :disabled="disabled" :aria-label="'查找'+caption" placeholder="输入名称搜索" maxlength="128" @keydown.enter.prevent="load()" /><button type="button" :disabled="disabled || reads.busy.value" @click="load()">查找</button></div></label>
<div class="compact-filters"><select v-model="selected" :aria-label="caption" :disabled="disabled || reads.busy.value"><option value="">请选择</option><option v-for="c in choices" :key="c.id" :value="c.id" :disabled="c.role && !c.role.isEnabled">{{ c.name }}</option></select><button type="button" :disabled="disabled || !selected" @click="choose">添加</button><button v-if="next" type="button" :disabled="disabled || reads.busy.value" @click="load(next)">更多</button></div>
<p v-if="error" class="error" role="alert">{{ error }}</p><small v-else-if="reads.busy.value" class="muted">读取候选中…</small><small v-else-if="!choices.length" class="muted">无符合条件的对象。</small></div></template>
