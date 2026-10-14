<script setup lang="ts">
import { computed } from 'vue'
import type { RunItem, Runtime } from './types'

const props = defineProps<{ runtime: Runtime; countdown: number; confirmationPending: boolean }>()
const emit = defineEmits<{ confirm: [taskId: string]; showFailures: [runId: string]; cancelCompletionAction: [] }>()

const center = computed(() => props.runtime.runCenter)
const visible = computed(() => Boolean(props.runtime.run))
const isFinished = computed(() => ['completed', 'stopped'].includes(props.runtime.run?.state || ''))
const failures = computed(() => (props.runtime.run?.items || []).filter((item) => item.state === 'failed'))
const terminalCount = computed(() => (props.runtime.run?.items || []).filter((item) => ['succeeded', 'failed', 'stopped'].includes(item.state)).length)
const progress = computed(() => isFinished.value ? terminalCount.value : center.value?.currentIndex || 0)
const currentScript = computed(() => isFinished.value ? (failures.value[0]?.script_name || props.runtime.run?.queue_name || '本轮运行') : center.value?.currentScriptName || '正在整理运行结果')
const currentTask = computed(() => isFinished.value ? (failures.value[0]?.task_name || '全部程序已处理') : center.value?.currentTaskName || '等待下一程序')
function itemReason(item: RunItem) { return item.error_summary || (item.exit_code !== null && item.exit_code !== undefined ? `退出码 ${item.exit_code}` : '未提供错误摘要') }
const stateText = computed(() => {
  if (!props.runtime.run) return ''
  if (props.confirmationPending) return '确认已接收，正在请求程序正常关闭'
  if (center.value?.currentState === 'closing') return '正在请求程序正常关闭'
  if (center.value?.currentState === 'force_closing') return '正在收尾受管进程'
  if (center.value?.currentState === 'cooling_down') return `第 ${center.value.currentIndex} 项已完成，${props.countdown} 秒后启动下一程序`
  if (props.runtime.run.state === 'completed') return failures.value.length ? '整轮执行完成，存在失败项' : '整轮执行完成'
  if (props.runtime.run.state === 'stopped') return '本轮已停止，不会启动下一程序'
  if (center.value?.currentState === 'waiting_manual') return '等待人工确认当前程序完成'
  return center.value?.currentState || props.runtime.run.state
})
const statusKind = computed(() => {
  if (!props.runtime.run) return 'idle'
  if (props.runtime.run.state === 'completed') return failures.value.length ? 'failed' : 'completed'
  if (props.runtime.run.state === 'stopped') return 'stopped'
  return center.value?.currentState || 'running'
})
const statusLabel = computed(() => ({
  idle: '空闲', validating: '校验中', starting: '启动中', monitoring: '运行中', waiting_manual: '等待确认',
  closing: '正在收尾', force_closing: '正在收尾', cooling_down: '冷却中', completed: '已完成', failed: '存在失败', stopped: '已停止',
}[statusKind.value] || '运行中'))
const skips = computed(() => props.runtime.run?.scheduler_skips || [])
const completion = computed(() => props.runtime.completionAction)
const completionPending = computed(() => ['delaying', 'countdown'].includes(completion.value?.status || ''))
const completionText = computed(() => {
  if (!completion.value) return ''
  const names: Record<string, string> = { shutdown: '关机', force_shutdown: '强制关机', restart: '重启', hibernate: '休眠', sleep: '睡眠', exit_app: '退出软件', logoff: '注销此账户' }
  const action = names[completion.value.action] || '完成后操作'
  if (completion.value.status === 'delaying') return `整轮成功，${completion.value.remainingSeconds} 秒后开始${action}倒计时`
  if (completion.value.status === 'countdown') return `将在 ${completion.value.remainingSeconds} 秒后${action}`
  return completion.value.message || ''
})
</script>

<template>
  <section v-if="visible" class="run-center" :class="[{ 'has-failures': failures.length }, 'state-' + statusKind]" aria-live="polite">
    <div class="run-center-title"><span class="run-state-badge">{{ statusLabel }}</span><div><b>运行中心</b><small>{{ runtime.run?.run_mode === 'plan' ? `计划：${runtime.run?.queue_name}` : runtime.run?.run_mode === 'all_scripts' ? '全部脚本' : runtime.run?.queue_name }}</small></div></div>
    <div class="run-center-progress"><b>{{ progress }}/{{ center?.totalPrograms || runtime.run?.items.length || 0 }}</b><span>{{ currentScript }}</span><strong>{{ currentTask }}</strong></div>
    <div class="run-center-state"><span>{{ stateText }}</span><b v-if="countdown > 0" class="countdown">{{ countdown }} 秒</b></div>
    <button v-if="runtime.activeTaskId && center?.currentState === 'waiting_manual'" class="confirm" :disabled="confirmationPending" @click="emit('confirm', runtime.activeTaskId)">{{ confirmationPending ? '确认已接收' : '确认当前程序完成' }}</button>
    <div v-if="completionText" class="run-center-completion" :class="completion?.status"><span>{{ completionText }}</span><button v-if="completionPending" @click="emit('cancelCompletionAction')">取消完成后操作</button></div>
    <div v-if="failures.length" class="run-center-failures" role="alert">
      <b>失败项</b>
      <ul><li v-for="item in failures" :key="item.task_id"><span>{{ item.script_name ? `${item.script_name} / ` : '' }}{{ item.task_name }}</span><small>{{ itemReason(item) }}</small></li></ul>
      <button @click="runtime.run && emit('showFailures', runtime.run.id)">查看失败详情</button>
    </div>
    <div v-if="skips.length" class="run-center-skips" role="status"><b>计划跳过提醒</b><span v-for="skip in skips" :key="skip">{{ skip }}</span></div>
  </section>
</template>
