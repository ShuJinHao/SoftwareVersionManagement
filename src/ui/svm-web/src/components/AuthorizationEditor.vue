<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Permission } from '../api'
import { type Authorization, type RoleAssignment, type AccessScope, label, productOperations, scopeLabel } from '../authorization'
import AuthorizationPicker from './AuthorizationPicker.vue'
const props=defineProps<{modelValue:Authorization;disabled?:boolean}>(),emit=defineEmits<{'update:modelValue':[Authorization]}>()
const scopeKinds=ref<Record<string,'Process'|'Device'>>({}),product=ref<{id:string;name:string}|null>(null)
const legacyGlobal=['identity.manage','software.create','asset.read','asset.manage']
const extra=computed(()=>props.modelValue.directPermissions.filter(p=>p.softwareId && productOperations.includes(p.operation)))
const legacy=computed(()=>props.modelValue.directPermissions.filter(p=>!p.softwareId || !productOperations.includes(p.operation)))
function update(roles=props.modelValue.roles,directPermissions=props.modelValue.directPermissions){emit('update:modelValue',{...props.modelValue,roles,directPermissions})}
function addRole(c:{id:string;name:string;role?:import('../authorization').Role}){if(!c.role || props.modelValue.roles.some(x=>x.roleId===c.id))return;update([...props.modelValue.roles,{roleId:c.id,name:c.role.name,isEnabled:c.role.isEnabled,revision:c.role.revision,operations:[...c.role.operations],scopes:[]}])}
function addScope(r:RoleAssignment,s:AccessScope){if(r.scopes.some(x=>x.kind===s.kind && x.resourceId===s.resourceId))return;update(props.modelValue.roles.map(x=>x.roleId===r.roleId?{...x,scopes:[...x.scopes,s]}:x))}
function removeScope(r:RoleAssignment,s:AccessScope){update(props.modelValue.roles.map(x=>x.roleId===r.roleId?{...x,scopes:x.scopes.filter(v=>v!==s)}:x))}
function removePermission(p:Permission){update(undefined,props.modelValue.directPermissions.filter(v=>v!==p))}
function toggleProduct(op:string,on:boolean){if(!product.value)return;const id=product.value.id,others=props.modelValue.directPermissions.filter(p=>p.softwareId!==id || p.operation!==op);emit('update:modelValue',{...props.modelValue,softwareNames:{...props.modelValue.softwareNames,[id]:product.value.name},directPermissions:on?[...others,{softwareId:id,operation:op}]:others})}
function global(op:string,on:boolean){const others=props.modelValue.directPermissions.filter(p=>p.softwareId!==null || p.operation!==op);update(undefined,on?[...others,{softwareId:null,operation:op}]:others)}
</script>
<template><div class="authorization-editor"><h3>角色与负责现场</h3><p class="muted">每个角色只在自己选定的范围生效。选择工序会覆盖其后新增设备，选择设备只覆盖该设备。</p>
<AuthorizationPicker kind="Role" caption="角色" :disabled="disabled" @choose="addRole" />
<article v-for="r in modelValue.roles" :key="r.roleId" class="authorization-card"><div class="section-heading"><strong>{{ r.name }}{{ r.isEnabled?'':'（已停用）' }}</strong><button type="button" class="text-button" :disabled="disabled" @click="update(modelValue.roles.filter(x=>x.roleId!==r.roleId))">移除此分配</button></div>
<small class="muted">{{ r.operations.map(label).join(' · ') }}</small>
<ul class="permission-list"><li v-for="s in r.scopes" :key="s.kind+String(s.resourceId)">{{ scopeLabel(s) }}<button type="button" :disabled="disabled" @click="removeScope(r,s)">移除范围</button></li></ul>
<p v-if="!r.scopes.length" class="error">为此角色选择至少一个范围。</p>
<div class="compact-filters"><button type="button" :disabled="disabled" @click="addScope(r,{kind:'Site',resourceId:null,name:'本厂全部现场'})">添加全厂现场</button><label>或按范围选择<select :value="scopeKinds[r.roleId]??'Process'" :disabled="disabled" @change="scopeKinds[r.roleId]=($event.target as HTMLSelectElement).value as 'Process'|'Device'"><option value="Process">工序</option><option value="Device">设备</option></select></label></div>
<AuthorizationPicker :kind="scopeKinds[r.roleId]??'Process'" caption="负责现场" :disabled="disabled" @choose="addScope(r,{kind:scopeKinds[r.roleId]??'Process',resourceId:$event.id,name:$event.name})" />
<details v-if="r.operations.some(o=>['release.upload','release.publish','release.disable'].includes(o))"><summary>此角色的独立产品范围</summary><p class="muted">现场范围不会授予上传、发布或停用权。需要这些职责时，在这里明确选择产品。</p><AuthorizationPicker kind="Software" caption="角色负责的产品" :disabled="disabled" @choose="addScope(r,{kind:'Software',resourceId:$event.id,name:$event.name})" /></details>
</article>
<h3>额外产品权限</h3><p class="muted">按软件单独添加，默认不勾选任何操作。</p><AuthorizationPicker kind="Software" caption="授权产品" :disabled="disabled" @choose="product=$event" />
<div v-if="product" class="authorization-card"><strong>{{ product.name }}</strong><div class="grant-options"><label v-for="op in productOperations" :key="op" class="check"><input type="checkbox" :disabled="disabled" :checked="modelValue.directPermissions.some(p=>p.softwareId===product!.id && p.operation===op)" @change="toggleProduct(op,($event.target as HTMLInputElement).checked)" />{{ label(op) }}</label></div></div>
<ul class="permission-list"><li v-for="p in extra" :key="p.softwareId+p.operation">{{ modelValue.softwareNames?.[p.softwareId!] ?? '已授权产品' }} · {{ label(p.operation) }}<button type="button" :disabled="disabled" @click="removePermission(p)">撤销此项</button></li></ul>
<details><summary>高级：既有直接授权（{{ legacy.length }} 项）</summary><p class="muted">直接授权与角色取并集。要收窄访问范围，请同时移除这里仍生效的旧授权。</p><div class="grant-options"><label v-for="op in legacyGlobal" :key="op" class="check"><input type="checkbox" :disabled="disabled" :checked="modelValue.directPermissions.some(p=>p.softwareId===null && p.operation===op)" @change="global(op,($event.target as HTMLInputElement).checked)" />全厂 · {{ label(op) }}</label></div>
<ul class="permission-list"><li v-for="p in legacy.filter(p=>p.softwareId)" :key="p.softwareId+p.operation">{{ modelValue.softwareNames?.[p.softwareId!] ?? '既有产品' }} · {{ label(p.operation) }}<button type="button" :disabled="disabled" @click="removePermission(p)">撤销此项</button></li></ul></details>
</div></template>
