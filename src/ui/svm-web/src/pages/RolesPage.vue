<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { request, ApiError } from '../api'
import { type Role, operationGroups, label } from '../authorization'
import type { Page } from '../catalog'
import { useSession } from '../session'
import { useOwnedReads } from '../ownedReads'
import { useProtectedMutation } from '../protectedMutation'
import OperationStatus from '../components/OperationStatus.vue'
const session=useSession(),mutation=useProtectedMutation(),rows=ref<Role[]>([]),search=ref(''),applied=ref(''),next=ref<string|null>(null),error=ref(''),notice=ref('')
const draft=ref<{role:Role|null;epoch:number}|null>(null),dialog=ref<HTMLDialogElement|null>(null),name=ref(''),enabled=ref(true),operations=ref<string[]>([]),reason=ref('')
const reads=useOwnedReads(()=>{rows.value=[];next.value=null})
const valid=computed(()=>draft.value?.epoch===reads.epoch.value && session.canManage)
async function failed(e:unknown){error.value=e instanceof Error?e.message:'角色读取失败';if(e instanceof ApiError && [401,403].includes(e.status))await session.load()}
async function load(cursor?:string|null){const q=new URLSearchParams({pageSize:'50'}),filter=cursor?applied.value:search.value.trim();if(filter)q.set('name',filter);if(cursor)q.set('cursor',cursor);error.value=''
 await reads.run('roles',!cursor,s=>request<Page<Role>>('/api/v1/manage/roles?'+q,'GET',undefined,undefined,undefined,{signal:s}),p=>{rows.value=cursor?[...rows.value,...p.items.filter(r=>!rows.value.some(x=>x.id===r.id))]:p.items;next.value=p.nextCursor;applied.value=filter},failed)}
async function open(role:Role|null){if(draft.value||mutation.blocked.value)return
 if(role){const id=role.id,current={value:null as Role|null};await reads.run('detail',true,s=>request<Role>('/api/v1/manage/roles/'+id,'GET',undefined,undefined,undefined,{signal:s}),r=>{current.value=r},failed);if(!current.value)return;role=current.value}
 draft.value={role:role?JSON.parse(JSON.stringify(role)) as Role:null,epoch:reads.epoch.value};name.value=role?.name??'';enabled.value=role?.isEnabled??true;operations.value=[...(role?.operations??[])];reason.value='';mutation.error.value='';await nextTick();dialog.value?.showModal()}
function close(){if(mutation.blocked.value)return;dialog.value?.close();draft.value=null}
async function save(){const d=draft.value;if(!d||mutation.busy.value||!valid.value&&!mutation.uncertain.value)return
 const p=mutation.pending.value,body=p?.body??{name:name.value,isEnabled:enabled.value,operations:operations.value,reason:reason.value,...(d.role?{expectedRevision:d.role.revision}:{})}
 if(!d.role&&!p)delete (body as {isEnabled?:boolean}).isEnabled
 await mutation.perform(p?.path??('/api/v1/manage/roles'+(d.role?'/'+d.role.id:'')),p?.method??(d.role?'PATCH':'POST'),body,session.current?.csrfToken??'')
 if(mutation.completed.value){close();notice.value='角色已保存，所有已分配人员统一生效。';await session.load();if(session.canManage)await load()}
 else if(!mutation.uncertain.value)await session.load()}
onMounted(()=>load())
</script>
<template><section class="workspace"><div class="page-heading"><div><h2>角色</h2><p class="muted">集中维护职责；角色名称不授予权限，具体操作和负责范围共同决定访问。</p></div><button class="primary" :disabled="!!draft||mutation.blocked.value" @click="open(null)">新增角色</button></div>
<p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="error" class="error" role="alert">{{ error }}</p><section class="panel"><form class="filters" @submit.prevent="load()"><label>角色名称<input v-model="search" maxlength="128" /></label><button :disabled="reads.busy.value">查询</button></form>
<div class="table-scroll" tabindex="0" role="region" aria-label="角色列表"><table><thead><tr><th>角色</th><th>状态</th><th>已分配人员</th><th>职责</th><th>操作</th></tr></thead><tbody><tr v-for="r in rows" :key="r.id"><td><strong>{{ r.name }}</strong></td><td>{{ r.isEnabled?'启用':'停用' }}</td><td>{{ r.assignedPeople }} 人</td><td>{{ r.operations.map(label).join('、') || '未配置操作' }}</td><td><button :disabled="!!draft||mutation.blocked.value" @click="open(r)">编辑</button></td></tr></tbody></table></div><p v-if="!rows.length" class="empty">{{ reads.busy.value?'正在读取角色…':'暂无符合条件的角色。' }}</p><button v-if="next" :disabled="reads.busy.value" @click="load(next)">更多角色</button></section>
<dialog v-if="draft" ref="dialog" class="authorization-dialog" aria-label="编辑角色职责" @cancel.prevent="close"><div class="dialog-heading"><h2>{{ draft.role?'编辑角色':'新增角色' }}</h2><button aria-label="关闭" :disabled="mutation.blocked.value" @click="close">×</button></div><p class="notice" v-if="draft.role">本次修改将统一影响 {{ draft.role.assignedPeople }} 名已分配人员。停用后分配记录保留。</p><p v-if="!valid&&!mutation.uncertain.value" class="error">身份或授权已变化，请重新读取。</p>
<form @submit.prevent="save"><fieldset :disabled="mutation.blocked.value||!valid"><label>角色名称<input v-model="name" required maxlength="128" /></label><label v-if="draft.role" class="check"><input type="checkbox" v-model="enabled" />启用角色</label>
<section v-for="group in operationGroups" :key="group.name"><h3>{{ group.name }}</h3><div class="grant-options"><label v-for="op in group.operations" :key="op" class="check"><input type="checkbox" v-model="operations" :value="op" />{{ label(op) }}</label></div></section><label>操作原因<textarea v-model="reason" required maxlength="256" rows="2"></textarea></label></fieldset><p v-if="mutation.error.value" class="error" role="alert">{{ mutation.error.value }}</p><OperationStatus :mutation="mutation" /><div class="dialog-actions"><button v-if="!mutation.uncertain.value" type="button" :disabled="mutation.busy.value" @click="close">取消</button><button class="primary" :disabled="mutation.busy.value||!valid&&!mutation.uncertain.value">{{ mutation.uncertain.value?'核实原操作':'保存角色' }}</button></div></form></dialog>
</section></template>
