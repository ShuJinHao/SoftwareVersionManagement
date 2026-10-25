<script setup lang="ts">
import { computed, nextTick, onMounted, provide, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Layers3, LayoutGrid, Settings2, ChevronDown, LogOut, KeyRound } from '@lucide/vue'
import { ApiError, request } from './api'
import { useSession } from './session'
import { pendingWriteKey } from './mutation'
import PasswordPage from './pages/PasswordPage.vue'
import MyAuthorization from './components/MyAuthorization.vue'
const session = useSession(), route = useRoute(), router = useRouter()
const error = ref(''), busy = ref(false), accountOpen=ref(false), passwordOpen=ref(false), passwordDialog=ref<HTMLDialogElement | null>(null), passwordBusy=ref(false)
const pendingWrite = ref(false)
const permissionsOpen=ref(false),permissionsDialog=ref<HTMLDialogElement|null>(null)
provide(pendingWriteKey, pendingWrite)
const authenticated = computed(() => session.current?.authenticated === true)
const canWork = computed(()=>session.canAssets || session.canSoftware || session.canInstances || session.canEnrollment)
const home = computed(() => canWork.value ? '/workspace' : '/system')
const allowed = computed(() => route.path === '/password' || route.path === '/system' || route.path === '/workspace' && canWork.value)
watch(() => [session.ready, authenticated.value, session.current?.mustChangePassword, allowed.value, home.value, route.path, pendingWrite.value], () => {
  if (!session.ready || session.error || pendingWrite.value) return
  if (!authenticated.value) { if (route.path !== '/login') void router.replace('/login') }
  else if (session.current?.mustChangePassword) { if (route.path !== '/password') void router.replace('/password') }
  else if (route.path === '/login' || !allowed.value) void router.replace(home.value)
}, { immediate: true })
onMounted(session.load)
async function logout() {
  busy.value = true; error.value = ''
  try { await request('/api/v1/session', 'DELETE', undefined, session.current?.csrfToken); await session.load() }
  catch (e) { error.value = e instanceof ApiError ? e.message : '退出未完成。'; await session.load() }
  finally { busy.value = false;accountOpen.value=false }
}
let focus:HTMLElement|null=null
async function openPassword(){if(pendingWrite.value)return;focus=document.activeElement as HTMLElement;accountOpen.value=false;passwordOpen.value=true;await nextTick();passwordDialog.value?.showModal()}
function closePassword(){if(passwordBusy.value)return;passwordDialog.value?.close();passwordOpen.value=false;focus?.focus()}
async function openPermissions(){if(pendingWrite.value)return;focus=document.activeElement as HTMLElement;accountOpen.value=false;permissionsOpen.value=true;await nextTick();permissionsDialog.value?.showModal()}
function closePermissions(){permissionsDialog.value?.close();permissionsOpen.value=false;focus?.focus()}
</script>
<template>
  <div :class="['platform-shell',{'has-sidebar':authenticated && !session.current?.mustChangePassword}]">
    <aside v-if="authenticated && !session.current?.mustChangePassword" class="platform-sidebar"><RouterLink :to="home" class="platform-brand"><span class="platform-logo"><Layers3 :size="23" /></span><span>软件管控<small>SOFTWARE WORKSPACE</small></span></RouterLink>
      <nav aria-label="主导航"><span class="nav-label">工作空间</span><RouterLink v-if="canWork" to="/workspace" :aria-disabled="pendingWrite" :class="{active:route.path==='/workspace'}"><LayoutGrid :size="18" />软件工作台</RouterLink><RouterLink to="/system" :aria-disabled="pendingWrite" :class="{active:route.path==='/system'}"><Settings2 :size="18" />系统管理</RouterLink></nav>
      <div class="sidebar-foot"><span class="site-dot"></span>厂内软件管理<small>版本、安装位置与现场事实</small></div>
    </aside>
    <div class="platform-main"><header class="platform-topbar"><span class="breadcrumbs">制造业软件版本管控平台<span>/</span><strong>{{ route.path==='/system'?'系统管理':authenticated?'软件工作台':'人员登录' }}</strong></span><div v-if="authenticated" class="account-control"><button class="account-button" :disabled="pendingWrite || busy" :aria-expanded="accountOpen" @click="accountOpen=!accountOpen"><span class="avatar">{{ session.current?.displayName?.slice(0,1) }}</span><span>{{ session.current?.displayName }}<small>{{ session.current?.employeeNo }}</small></span><ChevronDown :size="14" /></button><div v-if="accountOpen" class="account-menu"><button v-if="!session.current?.mustChangePassword" @click="openPermissions">我的权限</button><button v-if="!session.current?.mustChangePassword" @click="openPassword"><KeyRound :size="15" />修改本人密码</button><button @click="logout"><LogOut :size="15" />退出登录</button></div></div></header>
      <div v-if="!session.ready" class="state-card" role="status">正在读取会话…</div><div v-else-if="session.error" class="state-card"><p role="alert">{{ session.error }}</p><button @click="session.load">重新加载</button></div>
      <main v-else class="platform-content"><p v-if="error" role="alert" class="error">{{ error }}</p><RouterView v-if="!authenticated && route.path==='/login' || authenticated && (pendingWrite || allowed && (route.path==='/password' || !session.current?.mustChangePassword))" /></main>
    </div>
  </div>
  <dialog v-if="passwordOpen" ref="passwordDialog" aria-label="修改本人密码" @cancel.prevent="closePassword"><button class="dialog-close" aria-label="关闭改密" :disabled="passwordBusy" @click="closePassword">×</button><PasswordPage embedded @completed="closePassword" @busy="passwordBusy=$event" /></dialog>
  <dialog v-if="permissionsOpen" ref="permissionsDialog" class="authorization-dialog" aria-label="我的权限" @cancel.prevent="closePermissions"><button class="dialog-close" aria-label="关闭我的权限" @click="closePermissions">×</button><MyAuthorization /></dialog>
</template>
