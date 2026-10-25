<script setup lang="ts">
import type { Installation } from '../hardware'
import { categoryName } from '../catalog'
import { freshnessName, runningName, formatTime } from '../instances'
import { useSession } from '../session'
const session = useSession()
defineProps<{ rows: Installation[]; selected: Set<string>; disabled: boolean; timeZone: string; showDevice?: boolean }>()
defineEmits<{ toggle: [Installation]; software: [Installation]; open: [Installation, 'instances' | 'releases' | 'deployments']; grant: [Installation]; bind: [Installation]; revoke: [Installation] }>()
</script>
<template>
  <div class="table-wrap" tabindex="0" role="region" aria-label="软件安装位置与状态">
    <table class="installation-table"><thead><tr><th>选择</th><th v-if="showDevice">设备 / 工控机</th><th>软件</th><th>实际安装版本</th><th>运行 / 接入</th><th>上报 IP</th><th>最后上报</th><th>最近更新</th><th>操作</th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.rowId">
        <td><input type="checkbox" :checked="!!row.instance && selected.has(row.instance.id)" :disabled="disabled || !row.instance || !row.position || !session.canAt('deployment.create',row.software.id,row.device.processId,row.device.id)" :aria-label="`选择 ${row.device.name} / ${row.hardware?.name ?? '待补充归属'} / ${row.software.name}`" @change="$emit('toggle', row)" /></td>
        <td v-if="showDevice"><strong>{{ row.device.name }}</strong><small>{{ row.device.processName }} · {{ row.device.deviceNo }}</small><small>{{ row.hardware?.name ?? '待补充硬件归属' }}</small></td>
        <td><button class="text-button" :disabled="disabled" @click="$emit('software', row)">{{ row.software.name }}</button><small>{{ categoryName(row.software.category) }}</small></td>
        <td>{{ row.instance?.lastSnapshot?.installedVersion ?? (row.instance?.lastSnapshot?.installationState === 'NotInstalled' ? '尚未安装' : row.instance?.lastSnapshot ? '版本未知' : row.instance ? '尚未上报' : '尚未登记') }}<small v-if="row.instance?.latestAvailableFormalReleaseId">有可用正式版本</small></td>
        <td><span class="badge" :class="row.instance?.freshness === 'Fresh' ? 'enabled' : 'disabled'">{{ row.instance ? row.instance.freshness === 'Fresh' ? runningName(row.instance.lastSnapshot?.runningState) : freshnessName(row.instance.freshness) : '尚未登记' }}</span><small v-if="row.instance">{{ row.instance.lifecycle === 'Active' ? '接入启用' : '接入暂停' }}</small><small v-if="!row.position">待补充归属</small></td>
        <td>{{ row.instance?.lastSnapshot?.reportedIps.join('、') || '—' }}</td>
        <td>{{ row.instance?.lastAcceptedAt ? formatTime(row.instance.lastAcceptedAt, timeZone) : '—' }}<small v-if="row.instance?.freshness === 'Unknown'">已 {{ row.instance.unreportedSeconds ?? '—' }} 秒未上报</small></td>
        <td><button v-if="row.instance?.latestTaskId" class="text-button" :disabled="disabled" @click="$emit('open',row,'deployments')">{{ row.instance.latestTaskResult ?? '查看进度' }}</button><span v-else>—</span></td>
        <td><div class="row-actions"><button :disabled="disabled" @click="$emit('open',row,'releases')">版本与来源</button>
          <button v-if="row.instance" :disabled="disabled" @click="$emit('open', row, 'instances')">详情与接入</button>
          <button v-else-if="row.position && session.canAt('enrollment.manage',row.software.id,row.device.processId,row.device.id)" :disabled="disabled" @click="$emit('grant',row)">接入软件</button>
          <button v-if="!row.position && row.instance && session.canAt('binding.manage',row.software.id,row.device.processId,row.device.id)" :disabled="disabled" @click="$emit('bind',row)">补充归属</button>
          <button v-if="row.position && !row.instance && session.canAt('binding.manage',row.software.id,row.device.processId,row.device.id)" :disabled="disabled" @click="$emit('revoke',row)">解除关联</button>
        </div></td>
      </tr></tbody>
    </table>
    <p v-if="!rows.length" class="empty">暂无有权查看的软件安装位置。</p>
  </div>
</template>
