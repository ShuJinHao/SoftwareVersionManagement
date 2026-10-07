import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { ApiError, request, type Session } from './api'

export const useSession = defineStore('session', () => {
  const current = ref<Session | null>(null)
  const ready = ref(false)
  const error = ref('')
  const canManage = computed(() => current.value?.authenticated && !current.value.mustChangePassword &&
    current.value.permissions.some(p => p.softwareId === null && p.operation === 'identity.manage'))
  function can(operation: string, softwareId: string | null = null) { return current.value?.authenticated === true && !current.value.mustChangePassword && current.value.permissions.some(p => p.softwareId === softwareId && p.operation === operation) }
  const canAssets = computed(() => can('asset.read'))
  const canSoftware = computed(() => can('software.create') || current.value?.authenticated && !current.value.mustChangePassword && current.value.permissions.some(p => p.softwareId !== null && p.operation === 'software.read'))
  const canInstances = computed(() => current.value?.authenticated && !current.value.mustChangePassword && current.value.permissions.some(p => p.softwareId !== null && p.operation === 'instance.read'))
  const canEnrollment = computed(() => current.value?.authenticated && !current.value.mustChangePassword && current.value.permissions.some(p => p.softwareId !== null && p.operation === 'enrollment.manage'))
  async function load() {
    error.value = ''
    try { current.value = await request<Session>('/api/v1/session') }
    catch (e) {
      if (e instanceof ApiError && e.code === 'AUTHENTICATION_REQUIRED') {
        // The server clears the expired cookie. Only repeat this read, never a write.
        current.value = null
        try { current.value = await request<Session>('/api/v1/session') }
        catch (failure) { error.value = failure instanceof ApiError ? failure.message : '会话加载失败，请重试。' }
      } else { current.value = null; error.value = e instanceof ApiError ? e.message : '会话加载失败，请重试。' }
    } finally { ready.value = true }
  }
  return { current, ready, error, canManage, canAssets, canSoftware, canInstances, canEnrollment, can, load }
})
