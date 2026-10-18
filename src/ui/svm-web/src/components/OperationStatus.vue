<script setup lang="ts">
import type { useMutation } from '../mutation'
defineProps<{ mutation: ReturnType<typeof useMutation> }>()
</script>
<template>
  <div v-if="mutation.operationId.value" class="operation-status" role="status" aria-live="polite">
    <span>操作编号 <code>{{ mutation.operationId.value }}</code></span>
    <span v-if="mutation.traceId.value">追踪编号 <code>{{ mutation.traceId.value }}</code></span>
    <span v-if="mutation.busy.value">正在提交或核实…</span>
    <span v-else-if="mutation.uncertain.value">待核实</span>
  </div>
</template>
