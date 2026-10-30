<script setup lang="ts">
import type { Installation } from '../hardware'
import { useSession } from '../session'
const props=defineProps<{rows:Installation[];disabled:boolean}>()
const emit=defineEmits<{software:[row:Installation];open:[row:Installation,target:string];revoke:[row:Installation]}>()
const session=useSession()
const date=(v:string|undefined)=>v?new Date(v).toLocaleString():'—'
</script>
<template>
<div class="table-wrap" tabindex="0" role="region" aria-label="PLC 程序与人工确认版本"><table><thead><tr><th>设备 / 控制器</th><th>主程序库</th><th>人工确认版本</th><th>最新正式版</th><th>确认人员 / 现场时间</th><th>操作</th></tr></thead><tbody>
<tr v-for="row in props.rows" :key="row.rowId"><td>{{ row.device.name }}<small>{{ row.hardware?.name }}</small></td><td><button class="text-button" :disabled="disabled" @click="emit('software',row)">{{ row.software.name }}</button></td><td>{{ row.plcUsage?.current?.version ?? '待人工确认' }}<small v-if="row.plcUsage?.current">{{ row.plcUsage.current.releaseState === 'Formal' ? '正式版' : row.plcUsage.current.releaseState === 'Disabled' ? '已停用归档' : '测试版' }}</small></td><td>{{ row.latestFormalVersion ?? '尚无可用正式版' }}</td><td>{{ row.plcUsage?.current?.employeeNo ?? '—' }}<small>{{ date(row.plcUsage?.current?.fieldAt) }}</small></td><td><div class="row-actions"><button :disabled="disabled" @click="emit('open',row,'releases')">版本 / 下载</button><button v-if="row.position" :disabled="disabled" @click="emit('open',row,'plcUsage')">现场使用</button><button v-if="row.position && session.canAt('binding.manage',row.software.id,row.device.processId,row.device.id)" :disabled="disabled" @click="emit('revoke',row)">解除关联</button></div></td></tr>
</tbody></table><p v-if="!rows.length" class="empty">尚未关联主程序库。</p></div>
<p class="muted">现场版本由人员确认；下载不会改变控制器使用记录。</p>
</template>
