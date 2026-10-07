import { computed, inject, onMounted, onUnmounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { pendingWriteKey, useMutation } from './mutation'

export function useProtectedMutation() {
  const mutation = useMutation(), pending = inject(pendingWriteKey, ref(false))
  const blocked = computed(() => mutation.busy.value || mutation.uncertain.value)
  const completed = computed(() => !blocked.value && !mutation.error.value)
  watch(blocked, value => { pending.value = value }, { flush: 'sync' })
  onBeforeRouteLeave(() => { if (blocked.value) { mutation.error.value = '请先等待或核实原操作，再离开此页面。'; return false } })
  const unload = (event: BeforeUnloadEvent) => { if (blocked.value) { event.preventDefault(); event.returnValue = '' } }
  onMounted(() => window.addEventListener('beforeunload', unload))
  onUnmounted(() => { window.removeEventListener('beforeunload', unload); pending.value = false; mutation.dispose() })
  return { ...mutation, blocked, completed }
}
