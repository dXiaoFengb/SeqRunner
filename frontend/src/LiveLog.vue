<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { Runtime } from './types'
import { logLabel } from './logLabels'
type Phase = 'validation' | 'running' | 'waiting' | 'closing' | 'cooling' | 'success' | 'failed' | 'info'
type LiveLogEntry = { message: string; phase: Phase }
const props = defineProps<{ logs: Array<string | LiveLogEntry>; runtime?: Runtime; scriptLog?: { runId: string; taskId: string; text: string } | null }>()
const phaseLabels: Record<Phase, string> = { validation: '校验', running: '运行', waiting: '等待确认', closing: '收尾', cooling: '冷却', success: '完成', failed: '失败', info: '信息' }
const entries = computed(() => props.logs.map((entry): LiveLogEntry => typeof entry === 'string' ? { message: entry, phase: 'info' } : entry))
const current = computed(() => { const run = props.runtime?.run; if (!run || !props.runtime?.busy) return null; return run.items.find(item => item.task_id === props.runtime?.activeTaskId && ['monitoring', 'waiting_manual'].includes(item.state)) || null })
const summary = computed(() => { const run = props.runtime?.run; if (!run || !['completed', 'stopped'].includes(run.state) || !run.started_at || !run.ended_at) return ''; const seconds = Math.max(0, Math.floor((Date.parse(run.ended_at) - Date.parse(run.started_at)) / 1000)); if (!Number.isFinite(seconds)) return ''; const state = run.state === 'stopped' ? '本轮已停止' : run.items.some(item => item.state === 'failed') ? '本轮完成，存在失败项' : '本轮执行完成'; return `${state}，总耗时 ${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分 ${seconds % 60} 秒` })
const list = ref<HTMLElement | null>(null)
const scriptList = ref<HTMLElement | null>(null)
function scrollToEnd(element: HTMLElement | null) { if (element) element.scrollTop = element.scrollHeight }
async function scrollSummaryToEnd() { await nextTick(); scrollToEnd(list.value) }
async function scrollScriptToEnd() { await nextTick(); scrollToEnd(scriptList.value) }
onMounted(async () => { await scrollSummaryToEnd(); await scrollScriptToEnd() })
// 实时日志不保留浏览暂停状态：每条新增内容到达后都回到最新一行。
watch(() => [props.logs, summary.value] as const, scrollSummaryToEnd, { flush: 'pre' })
watch(() => props.runtime?.run?.id, scrollSummaryToEnd)
watch(() => [props.scriptLog, current.value?.task_id] as const, scrollScriptToEnd, { flush: 'pre' })
const scriptText = computed(() => current.value && props.scriptLog?.runId === props.runtime?.run?.id && props.scriptLog?.taskId === current.value.task_id ? props.scriptLog.text : '')
const hasScriptLog = computed(() => Boolean(current.value && (current.value.log_source_configured || current.value.ui_log_source_configured || scriptText.value)))
</script>
<template>
  <section class="side-live-log" :class="{ 'has-script-log': runtime?.busy }" aria-label="本轮日志摘要">
    <div class="side-live-header scheduler-header"><b>本轮日志摘要</b></div>
    <div ref="list" class="side-live-list scheduler-live-list" tabindex="0"><span v-if="!entries.length" class="side-live-empty">暂无实时日志</span><ol v-else><li v-for="(entry, index) in entries" :key="index" :class="'log-phase-' + entry.phase"><b class="log-phase">{{ phaseLabels[entry.phase] }}</b><span>{{ logLabel(entry.message) }}</span></li></ol><span v-if="summary" class="side-live-summary">{{ summary }}</span></div>
    <template v-if="runtime?.busy && hasScriptLog">
      <div class="side-live-header script-header"><b>{{ current ? `${current.task_name} · 脚本日志` : '脚本日志' }}</b></div>
      <div ref="scriptList" class="side-live-list script-live-list" tabindex="0"><span class="side-live-empty">{{ current ? (current.ui_log_status || '暂无法可靠获取该脚本的界面日志。') : '等待脚本运行' }}</span><pre v-if="scriptText" class="script-text">{{ scriptText }}</pre></div>
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
.scheduler-live-list ol{display:grid;gap:6px;margin:0;padding-left:0;list-style:none}.scheduler-live-list li{display:flex;align-items:flex-start;gap:6px;line-height:1.45}.log-phase{flex:none;min-width:48px;padding:1px 4px;border:1px solid #496577;border-radius:3px;background:#1a2b37;color:#b7cfdd;font-size:11px;text-align:center}.log-phase-validation .log-phase{border-color:#3a7896;background:#123448;color:#9ce4ff}.log-phase-running .log-phase{border-color:#287f8b;background:#123f45;color:#8cf1e6}.log-phase-waiting .log-phase{border-color:#9a792e;background:#3a2d12;color:#ffe098}.log-phase-closing .log-phase{border-color:#8b658f;background:#32223a;color:#e7c0f1}.log-phase-cooling .log-phase{border-color:#6972a2;background:#242943;color:#c3ccff}.log-phase-success .log-phase{border-color:#398161;background:#123525;color:#9af0c1}.log-phase-failed .log-phase{border-color:#9d505b;background:#3a1e25;color:#ffb5bd}
</style>
