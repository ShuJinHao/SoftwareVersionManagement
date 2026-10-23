<script setup lang="ts">
import { computed, ref } from 'vue'
import { useSession } from '../session'
import { operationNames } from '../catalog'
import UsersPage from './UsersPage.vue'
const session=useSession(), expanded=ref(false)
const globalNames:Record<string,string>={'identity.manage':'人员及授权管理','software.create':'创建软件','asset.read':'查看现场台账','asset.manage':'维护现场台账'}
const permissions=computed(()=>session.current?.permissions ?? [])
</script>
<template><section class="system-workspace"><div class="work-heading"><div><div class="eyebrow">账号与访问权限</div><h1>系统管理</h1><p class="muted">查看当前权限，由人员管理员维护账号与授权。</p></div></div>
<section class="panel own-permissions"><div class="section-heading"><div><h2>{{ session.current?.displayName }}</h2><small>工号 {{ session.current?.employeeNo }} · {{ permissions.length }} 项有效授权</small></div><button @click="expanded=!expanded">{{ expanded ? '收起当前权限' : '查看当前权限' }}</button></div>
<div v-if="expanded" class="table-wrap" tabindex="0" role="region" aria-label="当前有效权限"><table><thead><tr><th>范围</th><th>权限</th><th>权限标识</th></tr></thead><tbody><tr v-for="p in permissions" :key="p.operation+String(p.softwareId)"><td>{{ p.softwareId ? '软件 '+p.softwareId : '本厂区' }}</td><td>{{ globalNames[p.operation] ?? operationNames[p.operation] ?? p.operation }}</td><td>{{ p.operation }}</td></tr></tbody></table><p v-if="!permissions.length" class="empty">当前无业务权限，请联系人员管理员。</p></div></section>
<UsersPage v-if="session.canManage" /><p v-else class="empty">当前账号没有人员管理权限，可查看本人有效权限。修改密码请使用右上角账户菜单。</p>
</section></template>
