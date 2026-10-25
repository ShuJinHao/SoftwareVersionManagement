<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, request } from '../api'
import type { Page } from '../catalog'
import { type Grant, grantStateName } from '../instances'
import { useSession } from '../session'
import OperationStatus from '../components/OperationStatus.vue'
import { useProtectedMutation } from '../protectedMutation'
import { useOwnedReads } from '../ownedReads'
const session=useSession(), route=useRoute(), mutation=useProtectedMutation()
const softwareId=String(route.query.softwareId ?? ''), rows=ref<Grant[]>([]), next=ref<string|null>(null), failure=ref(''), notice=ref('')
const target=ref<Grant|null>(null), epoch=ref(0), reason=ref(''), dialog=ref<HTMLDialogElement|null>(null)
const reads=useOwnedReads(()=>{rows.value=[];next.value=null})
const allowed=computed(()=>session.can('enrollment.manage',softwareId)), valid=computed(()=>epoch.value===reads.epoch.value && allowed.value)
async function load(cursor?:string|null){if(!allowed.value || mutation.blocked.value)return;const q=new URLSearchParams({softwareId,pageSize:'50'});if(cursor)q.set('cursor',cursor)
 await reads.run('grants',!cursor,s=>request<Page<Grant>>('/api/v1/manage/enrollment-grants?'+q,'GET',undefined,undefined,undefined,{signal:s}),p=>{rows.value=cursor?[...new Map([...rows.value,...p.items].map(x=>[x.id,x])).values()]:p.items;next.value=p.nextCursor},async e=>{failure.value=e instanceof Error?e.message:'读取失败。';if(e instanceof ApiError && [401,403].includes(e.status))await session.load()})}
let previous:HTMLElement|null=null
async function revoke(grant:Grant){if(mutation.blocked.value)return;previous=document.activeElement as HTMLElement;target.value={...grant};epoch.value=reads.epoch.value;reason.value='';mutation.error.value='';await nextTick();dialog.value?.showModal()}
function close(){if(mutation.blocked.value)return;dialog.value?.close();target.value=null;previous?.focus()}
async function save(){const x=target.value;if(!x || !valid.value && !mutation.uncertain.value)return;await mutation.perform(`/api/v1/manage/enrollment-grants/${x.id}/revoke`,'POST',{expectedRevision:x.revision,reason:reason.value},session.current?.csrfToken ?? '');if(mutation.completed.value){close();notice.value='许可已撤销，已有实例身份继续保留。';await load()}}
onMounted(()=>load())
</script>
<template><section class="workspace"><div class="page-heading"><div><div class="eyebrow">当前软件 / 接入管理</div><h1>接入许可记录</h1><p class="muted">新许可从具体工控机的软件位置签发。历史宽范围许可仅保留查询、撤销及原成功结果核实。</p></div><button :disabled="reads.busy.value || mutation.blocked.value" @click="load()">刷新</button></div>
<p v-if="notice" class="success" role="status">{{ notice }}</p><p v-if="failure" class="error" role="alert">{{ failure }}</p><p v-if="!allowed" class="empty">请选择已授权的软件后查看接入许可。</p>
<div v-else class="panel"><div class="table-wrap" tabindex="0" role="region" aria-label="接入许可记录"><table><thead><tr><th>许可标识</th><th>安装位置</th><th>使用</th><th>有效期</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="x in rows" :key="x.id"><td>{{ x.id }}</td><td>{{ x.hardwareBindingId ?? '历史宽范围（不可新增登记）' }}</td><td>{{ x.usedCount }} / {{ x.maxInstances }}</td><td>{{ x.expiresAt }}</td><td>{{ grantStateName(x.state) }}</td><td><button v-if="x.state!=='Revoked'" :disabled="mutation.blocked.value" @click="revoke(x)">撤销</button></td></tr></tbody></table><p v-if="!rows.length" class="empty">{{ reads.busy.value ? '正在读取…' : '暂无接入许可记录。' }}</p></div><footer class="pagination"><span>已显示 {{ rows.length }} 项</span><button v-if="next" :disabled="reads.busy.value || mutation.blocked.value" @click="load(next)">更多记录</button></footer></div>
<dialog ref="dialog" aria-labelledby="revoke-grant-title" @cancel.prevent="close"><template v-if="target"><div class="dialog-heading"><h2 id="revoke-grant-title">撤销接入许可</h2><button :disabled="mutation.blocked.value" aria-label="关闭" @click="close">×</button></div><p>许可 {{ target.id }}</p><form @submit.prevent="save"><fieldset :disabled="mutation.blocked.value || !valid"><label>原因<textarea v-model="reason" required maxlength="256" /></label></fieldset><p v-if="!valid" class="error">当前人员或权限已变化。</p><p v-if="mutation.error.value" role="alert" class="error">{{ mutation.error.value }}</p><OperationStatus :mutation="mutation"/><div class="dialog-actions"><button type="button" :disabled="mutation.blocked.value" @click="close">取消</button><button class="primary" :disabled="mutation.busy.value || !valid && !mutation.uncertain.value">{{ mutation.uncertain.value ? '核实原操作' : '确认撤销' }}</button></div></form></template></dialog>
</section></template>
