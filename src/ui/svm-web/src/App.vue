<script setup lang="ts">
import { computed, onMounted, provide, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, request } from './api'
import { useSession } from './session'
import { pendingWriteKey } from './mutation'
const session = useSession(), route = useRoute(), router = useRouter()
const error = ref(''), busy = ref(false)
const pendingWrite = ref(false)
provide(pendingWriteKey, pendingWrite)
const authenticated = computed(() => session.current?.authenticated === true)
const home = computed(() => session.canAssets ? '/site' : session.canSoftware ? '/software' : session.canManage ? '/users' : '/password')
const allowed = computed(() => route.path === '/password' || route.path === '/site' && session.canAssets || route.path === '/software' && session.canSoftware || route.path === '/users' && session.canManage)
watch(() => [session.ready, authenticated.value, session.current?.mustChangePassword, allowed.value, home.value, route.path, pendingWrite.value], () => {
  if (!session.ready || session.error) return
  if (pendingWrite.value) return
  if (!authenticated.value) { if (route.path !== '/login') void router.replace('/login') }
  else if (session.current?.mustChangePassword) { if (route.path !== '/password') void router.replace('/password') }
  else if (route.path === '/login' || !allowed.value) void router.replace(home.value)
}, { immediate: true })
onMounted(session.load)
async function logout() {
  busy.value = true; error.value = ''
  try { await request('/api/v1/session', 'DELETE', undefined, session.current?.csrfToken); await session.load() }
  catch (e) { error.value = e instanceof ApiError ? e.message : '退出未完成。'; await session.load() }
  finally { busy.value = false }
}
</script>
<template>
  <header class="topbar">
    <RouterLink class="brand" :to="home"><span class="brand-mark">SV</span><span>制造业软件版本管控平台<small>软件 · 版本 · 现场</small></span></RouterLink>
    <div v-if="authenticated" class="identity"><span>{{ session.current?.displayName }}<small>{{ session.current?.employeeNo }}</small></span><button :disabled="busy || pendingWrite" @click="logout">退出</button></div>
  </header>
  <div v-if="!session.ready" class="state-card" role="status">正在读取会话…</div>
  <div v-else-if="session.error" class="state-card"><p role="alert">{{ session.error }}</p><button @click="session.load">重新加载</button></div>
  <template v-else>
    <nav v-if="authenticated && !session.current?.mustChangePassword" class="navigation" aria-label="管理导航">
      <RouterLink v-if="session.canAssets" to="/site">现场台账</RouterLink><RouterLink v-if="session.canSoftware" to="/software">软件目录</RouterLink><RouterLink v-if="session.canManage" to="/users">人员账号</RouterLink><RouterLink to="/password">本人密码</RouterLink>
      <span v-if="!session.canManage && !session.canAssets && !session.canSoftware" class="muted">当前账号未获管理或台账权限</span>
    </nav>
    <main><p v-if="error" role="alert" class="error">{{ error }}</p><RouterView v-if="!authenticated && route.path === '/login' || authenticated && (pendingWrite || allowed && (route.path === '/password' || !session.current?.mustChangePassword))" /></main>
  </template>
</template>
