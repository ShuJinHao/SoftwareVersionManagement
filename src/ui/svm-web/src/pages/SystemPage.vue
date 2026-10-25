<script setup lang="ts">
import { inject, ref } from 'vue'
import { useSession } from '../session'
import { pendingWriteKey } from '../mutation'
import UsersPage from './UsersPage.vue'
import RolesPage from './RolesPage.vue'
const session=useSession(),tab=ref('users'),pending=inject(pendingWriteKey,ref(false))
</script>
<template><section class="system-workspace"><div class="work-heading"><div><div class="eyebrow">账号与职责</div><h1>系统管理</h1><p class="muted">角色决定可做的操作，负责范围决定可管理的现场。</p></div></div>
<template v-if="session.canManage || pending"><nav class="view-switch system-tabs" aria-label="系统管理内容"><button :class="{active:tab==='users'}" :disabled="pending" @click="tab='users'">人员</button><button :class="{active:tab==='roles'}" :disabled="pending" @click="tab='roles'">角色</button></nav><UsersPage v-if="tab==='users'" /><RolesPage v-else /></template>
<p v-else class="empty">当前账号没有人员管理权限。可从右上角账户菜单查看“我的权限”或修改本人密码。</p>
</section></template>
