<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { Runtime } from './types'
import { logLabel } from './logLabels'
const props = defineProps<{ logs: string[]; runtime?: Runtime; scriptLog?: { runId: string; taskId: string; text: string } | null }>()
const current = computed(() => { const run = props.runtime?.run; if (!run || !props.runtime?.busy) return null; return run.items.find(item => item.task_id === props.runtime?.activeTaskId && ['monitoring', 'waiting_manual'].includes(item.state)) || null })
const summary = computed(() => { const run = props.runtime?.run; if (!run || !['completed', 'stopped'].includes(run.state) || !run.started_at || !run.ended_at) return ''; const seconds = Math.max(0, Math.floor((Date.parse(run.ended_at) - Date.parse(run.started_at)) / 1000)); if (!Number.isFinite(seconds)) return ''; const state = run.state === 'stopped' ? '本轮已停止' : run.items.some(item => item.state === 'failed') ? '本轮完成，存在失败项' : '本轮执行完成'; return `${state}，总耗时 ${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分 ${seconds % 60} 秒` })
const list = ref<HTMLElement | null>(null)
const scriptList = ref<HTMLElement | null>(null)
const following = ref(true)
const scriptFollowing = ref(true)
function onScroll() { const el = list.value; if (el) following.value = el.scrollHeight - el.scrollTop - el.clientHeight <= 8 }
function toBottom() { const el = list.value; if (el) { el.scrollTop = el.scrollHeight; following.value = true } }
function onScriptScroll() { const el = scriptList.value; if (el) scriptFollowing.value = el.scrollHeight - el.scrollTop - el.clientHeight <= 8 }
function scriptToBottom() { const el = scriptList.value; if (el) { el.scrollTop = el.scrollHeight; scriptFollowing.value = true } }
onMounted(async () => { await nextTick(); toBottom(); scriptToBottom() })
watch(() => [props.logs, summary.value] as const, async () => { if (following.value) { await nextTick(); toBottom() } }, { flush: 'pre' })
watch(() => props.runtime?.run?.id, async (id, previous) => { if (id && id !== previous) { following.value = true; await nextTick(); toBottom() } })
watch(() => [props.runtime?.run?.id, current.value?.task_id] as const, () => { scriptFollowing.value = true })
watch(() => [props.scriptLog, current.value] as const, async () => { if (scriptFollowing.value) { await nextTick(); scriptToBottom() } }, { flush: 'pre' })
const scriptText = computed(() => current.value && props.scriptLog?.runId === props.runtime?.run?.id && props.scriptLog?.taskId === current.value.task_id ? props.scriptLog.text : '')
</script>
<template>
  <section class="side-live-log" :class="{ 'has-script-log': runtime?.busy }" aria-label="本轮日志摘要">
    <div class="side-live-header scheduler-header"><b>本轮日志摘要</b><button v-if="!following" type="button" @click="toBottom">回到底部</button></div>
    <div ref="list" class="side-live-list scheduler-live-list" tabindex="0" @scroll="onScroll"><span v-if="!logs.length" class="side-live-empty">暂无实时日志</span><ol v-else><li v-for="(line, index) in logs" :key="index">{{ logLabel(line) }}</li></ol><span v-if="summary" class="side-live-summary">{{ summary }}</span></div>
    <template v-if="runtime?.busy">
      <div class="side-live-header script-header"><b>{{ current ? `${current.task_name} · 脚本日志` : '脚本日志' }}</b><button v-if="!scriptFollowing && current" type="button" @click="scriptToBottom">回到底部</button></div>
      <div ref="scriptList" class="side-live-list script-live-list" tabindex="0" @scroll="onScriptScroll"><span class="side-live-empty">{{ current ? (current.ui_log_status || '暂无法可靠获取该脚本的界面日志。') : '等待脚本运行' }}</span><pre v-if="scriptText" class="script-text">{{ scriptText }}</pre></div>
    </template>
  </section>
</template>

<style scoped>
.script-live-list { background: #10241f; border-color: #327d65; }
.has-script-log .script-live-list { flex: 2 1 0; }
.has-script-log .scheduler-live-list { flex: 1 1 0; }
.has-script-log .script-header { margin-top: 8px; }
.side-live-summary { display: block; color: #8ce0bf; padding-top: 6px; }
.script-text { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; margin: 6px 0 0; }
</style>
