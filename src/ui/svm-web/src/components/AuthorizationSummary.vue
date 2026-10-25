<script setup lang="ts">
import { computed } from 'vue'
import { label, scopeLabel, fieldOperations, productOperations, type Authorization, type RoleAssignment } from '../authorization'
const props=defineProps<{ value:Authorization }>()
const products=computed(()=>Object.entries(props.value.directPermissions.filter(p=>p.softwareId).reduce<Record<string,import('../api').Permission[]>>((result,p)=>{(result[p.softwareId!]??=[]).push(p);return result},{})))
const fieldScopes=(r:RoleAssignment)=>r.scopes.filter(s=>s.kind!=='Software')
const productScopes=(r:RoleAssignment)=>r.scopes.filter(s=>s.kind==='Software')
const fieldLabels=(r:RoleAssignment)=>r.operations.filter(o=>fieldOperations.includes(o)).map(label).join(' · ')
const productLabels=(r:RoleAssignment)=>r.operations.filter(o=>productOperations.includes(o)).map(label).join(' · ')
</script>
<template><section class="authorization-summary">
  <p v-if="!value.roles.length && !value.directPermissions.length" class="empty">尚未分配职责，请联系人员管理员。</p>
  <article v-for="r in value.roles" :key="r.roleId" class="authorization-card"><div class="section-heading"><strong>{{ r.name }}</strong><span class="badge" :class="r.isEnabled?'enabled':'disabled'">{{ r.isEnabled?'生效':'角色已停用' }}</span></div>
    <p v-if="r.operations.includes('identity.manage') && r.scopes.some(s=>s.kind==='Site')">本厂 · 管理人员、角色与授权</p>
    <template v-if="fieldScopes(r).length && fieldLabels(r)"><p>{{ fieldScopes(r).map(scopeLabel).join('；') }}</p><small class="muted">{{ fieldLabels(r) }}</small></template>
    <template v-if="productScopes(r).length && productLabels(r)"><p>{{ productScopes(r).map(scopeLabel).join('；') }}</p><small class="muted">{{ productLabels(r) }}</small></template>
    <p v-if="r.operations.some(o=>o.startsWith('release.')) && !productScopes(r).length" class="muted">未指定产品范围，产品维护、发布与停用职责未生效。</p></article>
  <details v-if="value.directPermissions.length"><summary>既有直接授权 / 额外产品权限 · {{ value.directPermissions.length }} 项</summary><p class="muted">与角色权限共同生效；收窄角色不会撤销这些授权。</p>
    <ul class="permission-list"><li v-for="p in value.directPermissions.filter(p=>!p.softwareId)" :key="p.operation">本厂 · {{ label(p.operation) }}</li>
      <li v-for="[id,permissions] in products" :key="id"><strong>{{ value.softwareNames?.[id] ?? '已授权产品' }}</strong><small>{{ permissions?.map(p=>label(p.operation)).join(' · ') }}</small></li></ul></details>
  <p class="muted">现场职责仅作用于各自范围内的设备及后续关联软件。产品发布、停用须有明确产品授权；停用角色不删除历史分配。</p>
</section></template>
