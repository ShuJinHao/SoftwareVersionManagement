<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { ApiError, request, type User, type UserPage } from '../api'
import { useSession } from '../session'
import OperationStatus from '../components/OperationStatus.vue'
import { useProtectedMutation } from '../protectedMutation'
import { useOwnedReads } from '../ownedReads'
import AuthorizationSummary from '../components/AuthorizationSummary.vue'
import AuthorizationEditor from '../components/AuthorizationEditor.vue'
import type { Authorization } from '../authorization'
const session=useSession(), mutation=useProtectedMutation(),{busy,uncertain,error}=mutation
const users=ref<User[]>([]),selected=ref<User|null>(null),authorization=ref<Authorization|null>(null),readError=ref(''),notice=ref('')
const employeeFilter=ref(''),enabledFilter=ref(''),cursors=ref(['']),nextCursor=ref<string|null>(null),readAt=ref(''),applied=ref({employeeNo:'',isEnabled:''})
type Mode='create'|'edit'|'permissions'|'reset'
const draft=ref<{mode:Mode;user:User|null;epoch:number}|null>(null),dialog=ref<HTMLDialogElement|null>(null),authDraft=ref<Authorization|null>(null)
const employeeNo=ref(''),displayName=ref(''),temporaryPassword=ref(''),isEnabled=ref(true),reason=ref('')
const title=computed(()=>({create:'创建人员账号',edit:'修改账号资料',permissions:'分配角色与负责范围',reset:'重置账号密码'}[draft.value?.mode??'create']))
const reads=useOwnedReads(()=>{users.value=[];selected.value=null;authorization.value=null;nextCursor.value=null;temporaryPassword.value='';if(!uncertain.value)authDraft.value=null})
const valid=computed(()=>!!draft.value && draft.value.epoch===reads.epoch.value && session.canManage)
const get=<T,>(path:string,signal:AbortSignal)=>request<T>(path,'GET',undefined,undefined,undefined,{signal})
async function failed(e:unknown){readError.value=e instanceof Error?e.message:'读取失败，请重试。';if(e instanceof ApiError && [401,403].includes(e.status))await session.load()}
async function load(stack=cursors.value,filters=applied.value){const q=new URLSearchParams({pageSize:'50'});if(filters.employeeNo)q.set('employeeNo',filters.employeeNo);if(filters.isEnabled)q.set('isEnabled',filters.isEnabled);if(stack[stack.length-1])q.set('cursor',stack[stack.length-1]!);readError.value=''
 await reads.run('users',true,s=>get<UserPage>('/api/v1/manage/users?'+q,s),p=>{users.value=p.items;nextCursor.value=p.nextCursor;readAt.value=p.serverTime;cursors.value=stack;applied.value=filters},failed)}
async function select(id:string){if(draft.value || mutation.blocked.value)return;selected.value=null;authorization.value=null
 await reads.run('detail',true,async s=>{const [u,a]=await Promise.all([get<User>('/api/v1/manage/users/'+id,s),get<Authorization>('/api/v1/manage/subjects/'+id+'/authorization',s)]);return{u,a}},v=>{selected.value=v.u;authorization.value=v.a},failed)}
async function filter(){if(draft.value)return;reads.invalidate('detail');selected.value=null;authorization.value=null;await load([''],{employeeNo:employeeFilter.value.trim(),isEnabled:enabledFilter.value})}
async function open(mode:Mode){if(draft.value || mutation.blocked.value || !session.canManage)return
 if(mode!=='create'){const id=selected.value?.id;if(!id)return;await select(id);if(!selected.value||!authorization.value)return}
 draft.value={mode,user:selected.value?structuredClone({...selected.value,permissions:selected.value.permissions.map(p=>({...p}))}):null,epoch:reads.epoch.value}
 authDraft.value=authorization.value?JSON.parse(JSON.stringify(authorization.value)) as Authorization:null
 employeeNo.value='';displayName.value=selected.value?.displayName??'';temporaryPassword.value='';isEnabled.value=selected.value?.isEnabled??true;reason.value='';error.value=''
 await nextTick();dialog.value?.showModal()}
function close(){if(mutation.blocked.value)return;dialog.value?.close();draft.value=null;authDraft.value=null;temporaryPassword.value='';error.value=''}
async function save(){const d=draft.value;if(!d || busy.value || !valid.value&&!uncertain.value)return
 let path='/api/v1/manage/users',method='POST',body:unknown
 if(mutation.pending.value){({path,method,body}=mutation.pending.value)}
 else if(d.mode==='create')body={employeeNo:employeeNo.value,displayName:displayName.value,temporaryPassword:temporaryPassword.value}
 else {if(!d.user)return;const common={expectedRevision:d.user.revision,reason:reason.value}
  if(d.mode==='edit'){path+='/'+d.user.id;method='PATCH';body={...common,displayName:displayName.value,isEnabled:isEnabled.value}}
  else if(d.mode==='reset'){path+='/'+d.user.id+'/reset-password';body={...common,temporaryPassword:temporaryPassword.value}}
  else {const a=authDraft.value;if(!a)return;if(a.roles.some(r=>!r.scopes.length)){error.value='请为每个角色选择范围，或移除未分配范围的角色。';return}
   path='/api/v1/manage/subjects/'+d.user.id+'/authorization';method='PUT';body={expectedRevision:a.revision,reason:reason.value,assignments:a.roles.map(r=>({roleId:r.roleId,scopes:r.scopes.map(s=>({kind:s.kind,resourceId:s.resourceId}))})),directPermissions:a.directPermissions}}
 }
 await mutation.perform(path,method,body,session.current?.csrfToken??'')
 if(mutation.completed.value){const id=d.user?.id;close();notice.value='操作已完成。';await session.load();if(session.canManage){await load();if(id)await select(id)}}
 else if(!uncertain.value){temporaryPassword.value='';await session.load()}}
onMounted(()=>load())
</script>
<template><section class="workspace">
<div class="page-heading"><div><h2>人员账号</h2><p class="muted">先选择角色和负责现场；产品发布权限另行分配。</p></div><button class="primary" :disabled="!!draft || mutation.blocked.value" @click="open('create')">创建账号</button></div>
<p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="readError" class="error" role="alert">{{ readError }}</p>
<div class="account-layout"><section class="panel"><form class="filters" @submit.prevent="filter"><label>工号筛选<input v-model="employeeFilter" maxlength="64" placeholder="按工号前缀查找" /></label><label>账号状态<select v-model="enabledFilter"><option value="">全部状态</option><option value="true">启用</option><option value="false">停用</option></select></label><button :disabled="reads.pending('users') || !!draft">查询</button></form>
<div v-if="reads.pending('users')" class="empty" role="status">正在读取账号…</div><p v-else-if="!users.length" class="empty">没有符合条件的账号。</p>
<div v-if="users.length" class="table-scroll" tabindex="0" role="region" aria-label="人员列表"><table><thead><tr><th>人员</th><th>工号</th><th>账号状态</th><th>密码状态</th></tr></thead><tbody><tr v-for="u in users" :key="u.id" :class="{selected:selected?.id===u.id}"><td><button class="text-button" :disabled="!!draft || mutation.blocked.value" @click="select(u.id)">{{ u.displayName }}</button></td><td>{{ u.employeeNo }}</td><td><span class="badge" :class="u.isEnabled?'enabled':'disabled'">{{ u.isEnabled?'启用':'停用' }}</span></td><td>{{ u.mustChangePassword?'待首次改密':'已设置' }}</td></tr></tbody></table></div>
<footer class="pagination"><span>本页 {{ users.length }} 人<span v-if="readAt"> · {{ new Date(readAt).toLocaleTimeString() }}</span></span><div><button :disabled="reads.pending('users') || cursors.length<=1 || !!draft" @click="load(cursors.slice(0,-1))">上一页</button><button :disabled="reads.pending('users') || !nextCursor || !!draft" @click="load([...cursors,nextCursor!])">下一页</button></div></footer></section>
<aside class="panel detail"><p v-if="reads.pending('detail')" class="empty">正在读取详情…</p><template v-else-if="selected && authorization"><div class="eyebrow">账号与职责</div><h2>{{ selected.displayName }}</h2><p class="muted">{{ selected.employeeNo }} · {{ selected.isEnabled?'启用':'停用' }}</p><AuthorizationSummary :value="authorization" /><div class="detail-actions"><button class="primary" @click="open('permissions')">分配角色与范围</button><button @click="open('edit')">修改资料 / 启停</button><button @click="open('reset')">重置密码</button></div></template><p v-else class="empty">选择人员查看资料与有效权限。</p></aside></div>
<dialog v-if="draft" ref="dialog" class="authorization-dialog" aria-labelledby="account-dialog-title" @cancel.prevent="close"><div class="dialog-heading"><h2 id="account-dialog-title">{{ title }}</h2><button aria-label="关闭" :disabled="mutation.blocked.value" @click="close">×</button></div><p v-if="draft.user" class="muted">{{ draft.user.displayName }} · {{ draft.user.employeeNo }}</p><p v-if="!valid && !uncertain" class="error">当前身份或权限已变化，请关闭后重新读取。</p>
<form @submit.prevent="save"><fieldset :disabled="busy || uncertain || !valid"><label v-if="draft.mode==='create'">工号<input v-model="employeeNo" required maxlength="64" pattern="[A-Za-z0-9._-]+" autocomplete="off" /></label><label v-if="draft.mode==='create'||draft.mode==='edit'">显示名称<input v-model="displayName" required maxlength="128" /></label><label v-if="draft.mode==='edit'" class="check"><input v-model="isEnabled" type="checkbox" />启用账号</label><template v-if="draft.mode==='create'||draft.mode==='reset'"><label>临时密码<input v-model="temporaryPassword" type="password" required minlength="15" maxlength="128" autocomplete="new-password" /></label><p class="muted">下次登录须改密。重置密码会撤销全部旧会话。</p></template>
<AuthorizationEditor v-if="draft.mode==='permissions' && authDraft" v-model="authDraft" :disabled="busy||uncertain||!valid" />
<label v-if="draft.mode!=='create'">操作原因<textarea v-model="reason" required maxlength="256" rows="2"></textarea></label></fieldset>
<p v-if="uncertain" class="notice">结果仍待核实。原操作编号、对象和正文保持不变，请人工核实后再离开。</p><p v-if="error" class="error" role="alert">{{ error }}</p><OperationStatus :mutation="mutation" /><div class="dialog-actions"><button v-if="!uncertain" type="button" :disabled="busy" @click="close">取消</button><button class="primary" :disabled="busy || !valid&&!uncertain">{{ busy?'正在提交…':uncertain?'核实原操作':'保存' }}</button></div></form></dialog>
</section></template>
