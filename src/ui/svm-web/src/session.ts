import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { ApiError, request, type Session } from './api'
import { canAt as at, canAnyField, canGlobal, canProduct, fieldOperations, type Authorization } from './authorization'

export const useSession = defineStore('session', () => {
  const current = ref<Session | null>(null)
  const ready = ref(false)
  const error = ref('')
  const access = computed<Authorization>(()=>current.value?.authorization ?? {subjectId:current.value?.subjectId ?? '', revision:0,directPermissions:current.value?.permissions ?? [],roles:[]})
  const enabled = computed(()=>current.value?.authenticated === true && !current.value.mustChangePassword)
  const canManage = computed(() => enabled.value && canGlobal(access.value,'identity.manage'))
  // Software-level field checks only enable entry to server-filtered lists. Concrete actions use canAt.
  function can(operation: string, softwareId: string | null = null) { return enabled.value && (softwareId ? canProduct(access.value,operation,softwareId) || fieldOperations.includes(operation) && canAnyField(access.value,operation) : canGlobal(access.value,operation)) }
  function canAt(operation:string,softwareId:string|null=null,processId:string|null=null,deviceId:string|null=null) { return enabled.value && at(access.value,operation,softwareId,processId,deviceId) }
  function any(operation:string) {return enabled.value && canAnyField(access.value,operation)}
  const canAssets = computed(() => any('asset.read'))
  const canSoftware = computed(() => any('software.read') || any('software.create') || enabled.value && access.value.roles.some(r=>r.isEnabled && r.operations.includes('software.read') && r.scopes.some(s=>s.kind==='Software')))
  const canInstances = computed(() => any('instance.read'))
  const canEnrollment = computed(() => any('enrollment.manage'))
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
  return { current, ready, error, access, canManage, canAssets, canSoftware, canInstances, canEnrollment, can, canAt, any, load }
})
