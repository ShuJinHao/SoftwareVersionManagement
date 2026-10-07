<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, request, type Session } from '../api'
import { useSession } from '../session'
const session = useSession(), router = useRouter()
const employeeNo = ref(''), password = ref(''), busy = ref(false), error = ref('')
async function login() {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    await session.load()
    session.current = await request<Session>('/api/v1/session', 'POST', { employeeNo: employeeNo.value, password: password.value }, session.current?.csrfToken)
    password.value = ''
    await router.replace(session.current.mustChangePassword ? '/password' : session.canManage ? '/users' : '/password')
  } catch (e) { error.value = e instanceof ApiError ? e.message : '登录未完成，请检查连接。'; password.value = '' }
  finally { busy.value = false }
}
</script>
<template>
  <section class="auth-card">
    <div class="eyebrow">人员登录</div><h1>进入管理平台</h1>
    <p class="muted">使用本厂平台账号登录。</p>
    <form @submit.prevent="login">
      <label>工号<input v-model="employeeNo" autocomplete="username" required maxlength="64" :disabled="busy" /></label>
      <label>密码<input v-model="password" type="password" autocomplete="current-password" required maxlength="128" :disabled="busy" /></label>
      <p v-if="error" role="alert" class="error">{{ error }}</p>
      <button class="primary full" :disabled="busy || !session.current" type="submit">{{ busy ? '正在登录…' : '登录' }}</button>
    </form>
  </section>
</template>
