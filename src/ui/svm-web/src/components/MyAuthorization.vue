<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { request } from '../api'
import type { Authorization } from '../authorization'
import { useOwnedReads } from '../ownedReads'
import AuthorizationSummary from './AuthorizationSummary.vue'
const value=ref<Authorization|null>(null),error=ref(''),reads=useOwnedReads(()=>{value.value=null})
async function load(){value.value=null;error.value='';await reads.run('self',true,s=>request<Authorization>('/api/v1/session/authorization','GET',undefined,undefined,undefined,{signal:s}),v=>{value.value=v},e=>{error.value=e instanceof Error?e.message:'权限读取失败'})}
onMounted(load)
</script>
<template><section><h2>我的权限</h2><p v-if="reads.busy.value" role="status">正在读取当前有效授权…</p><p v-if="error" class="error" role="alert">{{ error }}</p><AuthorizationSummary v-if="value" :value="value" /><button type="button" :disabled="reads.busy.value" @click="load">重新读取权限</button></section></template>
