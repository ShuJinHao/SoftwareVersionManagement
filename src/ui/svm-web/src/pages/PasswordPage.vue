<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, request, type Session } from '../api'
import { useSession } from '../session'
const session = useSession(), router = useRouter()
const currentPassword = ref(''), newPassword = ref(''), confirmPassword = ref(''), busy = ref(false), error = ref(''), success = ref('')
async function change() {
  if (busy.value) return
  if (newPassword.value !== confirmPassword.value) { error.value = '两次输入的新密码不一致。'; return }
  busy.value = true; error.value = ''; success.value = ''
  try {
    session.current = await request<Session>('/api/v1/session/password', 'POST',
      { currentPassword: currentPassword.value, newPassword: newPassword.value }, session.current?.csrfToken)
    success.value = '密码已修改，旧会话已失效。'
    if (session.canAssets) await router.replace('/site')
    else if (session.canSoftware) await router.replace('/software')
    else if (session.canManage) await router.replace('/users')
  } catch (e) { error.value = e instanceof ApiError ? e.message : '修改未完成，请重新登录核实。' }
  finally { currentPassword.value = ''; newPassword.value = ''; confirmPassword.value = ''; busy.value = false }
}
</script>
<template>
  <section class="auth-card">
    <div class="eyebrow">账号安全</div><h1>{{ session.current?.mustChangePassword ? '首次登录，请修改密码' : '修改本人密码' }}</h1>
    <p class="muted">{{ session.current?.mustChangePassword ? '完成改密后即可使用已授权功能。' : '修改后，其他设备上的旧会话将失效。' }}</p>
    <form @submit.prevent="change">
      <label>当前密码<input v-model="currentPassword" type="password" autocomplete="current-password" required maxlength="128" :disabled="busy" /></label>
      <label>新密码<input v-model="newPassword" type="password" autocomplete="new-password" required minlength="15" maxlength="128" :disabled="busy" /></label>
      <label>确认新密码<input v-model="confirmPassword" type="password" autocomplete="new-password" required minlength="15" maxlength="128" :disabled="busy" /></label>
      <p v-if="error" role="alert" class="error">{{ error }}</p><p v-if="success" role="status" class="success">{{ success }}</p>
      <button class="primary full" :disabled="busy" type="submit">{{ busy ? '正在提交…' : '修改密码' }}</button>
    </form>
  </section>
</template>
