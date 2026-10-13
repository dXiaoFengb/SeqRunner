<script setup lang="ts">
import { computed } from 'vue'
import type { Runtime } from './types'

const props = defineProps<{ runtime: Runtime; countdown: number; confirmationPending: boolean }>()
const emit = defineEmits<{ confirm: [taskId: string] }>()

const center = computed(() => props.runtime.runCenter)
const visible = computed(() => Boolean(props.runtime.run))
const stateText = computed(() => {
  if (!props.runtime.run) return ''
  if (props.confirmationPending) return '确认已接收，正在请求程序正常关闭'
  if (center.value?.currentState === 'closing') return '正在请求程序正常关闭'
  if (center.value?.currentState === 'force_closing') return '正在收尾受管进程'
  if (center.value?.currentState === 'cooling_down') return `第 ${center.value.currentIndex} 项已完成，${props.countdown} 秒后启动下一程序`
  if (props.runtime.run.state === 'completed') return '整轮执行完成'
  if (props.runtime.run.state === 'stopped') return '本轮已停止，不会启动下一程序'
  if (center.value?.currentState === 'waiting_manual') return '等待人工确认当前程序完成'
  return center.value?.currentState || props.runtime.run.state
})
</script>

<template>
  <section v-if="visible" class="run-center" aria-live="polite">
    <div class="run-center-title"><span class="live-dot" :class="{ stopped: !runtime.busy }"></span><b>运行中心</b><small>{{ runtime.run?.run_mode === 'all_scripts' ? '全部脚本' : runtime.run?.queue_name }}</small></div>
    <div class="run-center-progress"><b>{{ center?.currentIndex || 0 }}/{{ center?.totalPrograms || runtime.run?.items.length || 0 }}</b><span>{{ center?.currentScriptName || '正在整理运行结果' }}</span><strong>{{ center?.currentTaskName || '等待下一程序' }}</strong></div>
    <div class="run-center-state"><span>{{ stateText }}</span><b v-if="countdown > 0" class="countdown">{{ countdown }} 秒</b></div>
    <button v-if="runtime.activeTaskId && center?.currentState === 'waiting_manual'" class="confirm" :disabled="confirmationPending" @click="emit('confirm', runtime.activeTaskId)">{{ confirmationPending ? '确认已接收' : '确认当前程序完成' }}</button>
  </section>
</template>
