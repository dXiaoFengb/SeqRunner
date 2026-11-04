<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { Runtime, ScriptReadableEvent } from './types'
import { logLabel } from './logLabels'

type Phase = 'validation' | 'running' | 'waiting' | 'closing' | 'cooling' | 'success' | 'failed' | 'info'
type LiveLogEntry = { message: string; phase: Phase }
const props = defineProps<{ logs: Array<string | LiveLogEntry>; runtime?: Runtime; scriptEvents?: ScriptReadableEvent[] }>()
const phaseLabels: Record<Phase, string> = { validation: '校验', running: '运行', waiting: '等待确认', closing: '收尾', cooling: '冷却', success: '完成', failed: '失败', info: '信息' }
const entries = computed(() => props.logs.map((entry): LiveLogEntry => typeof entry === 'string' ? { message: entry, phase: 'info' } : entry))
const current = computed(() => { const run = props.runtime?.run; if (!run || !props.runtime?.busy) return null; return run.items.find(item => item.task_id === props.runtime?.activeTaskId && ['monitoring', 'waiting_manual'].includes(item.state)) || null })
const scriptEvents = computed(() => (props.scriptEvents || []).filter((entry) => entry.runId === props.runtime?.run?.id && entry.taskId === current.value?.task_id && entry.event.source === 'script'))
const hasScriptLog = computed(() => Boolean(current.value?.log_source_configured || scriptEvents.value.length))
const readableProgress = computed(() => props.runtime?.runCenter?.readableProgress || '')
const summary = computed(() => { const run = props.runtime?.run; if (!run || !['completed', 'stopped'].includes(run.state) || !run.started_at || !run.ended_at) return ''; const seconds = Math.max(0, Math.floor((Date.parse(run.ended_at) - Date.parse(run.started_at)) / 1000)); if (!Number.isFinite(seconds)) return ''; const state = run.state === 'stopped' ? '本轮已停止' : run.items.some(item => item.state === 'failed') ? '本轮完成，存在失败项' : '本轮执行完成'; return `${state}，总耗时 ${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分 ${seconds % 60} 秒` })
const list = ref<HTMLElement | null>(null)
const scriptList = ref<HTMLElement | null>(null)
const compactOpen = ref(false)
const compactButton = ref<HTMLButtonElement | null>(null)
function closeCompact() { if (!compactOpen.value) return; compactOpen.value = false; void nextTick(() => compactButton.value?.focus()) }
function scrollToEnd(element: HTMLElement | null) { if (element) element.scrollTop = element.scrollHeight }
async function scrollSummaryToEnd() { await nextTick(); scrollToEnd(list.value) }
async function scrollScriptToEnd() { await nextTick(); scrollToEnd(scriptList.value) }
function levelLabel(event: ScriptReadableEvent) { return event.event.severity === 'error' ? '错误' : event.event.severity === 'warning' ? '警告' : '信息' }
onMounted(async () => { await scrollSummaryToEnd(); await scrollScriptToEnd() })
watch(() => [props.logs, summary.value] as const, scrollSummaryToEnd, { flush: 'pre' })
watch(() => props.runtime?.run?.id, scrollSummaryToEnd)
watch(() => [scriptEvents.value, current.value?.task_id] as const, scrollScriptToEnd, { flush: 'pre' })
</script>

<template>
  <section class="side-live-log" :class="{ 'has-script-log': runtime?.busy && hasScriptLog, 'compact-open': compactOpen }" aria-label="本软件运行状态" @keydown.esc.stop="closeCompact">
    <button ref="compactButton" class="side-live-toggle" :aria-expanded="compactOpen" aria-controls="side-live-panel" @click="compactOpen = !compactOpen">状态</button>
    <div id="side-live-panel" class="side-live-panel">
    <div class="side-live-header scheduler-header"><b>本软件运行状态</b><small v-if="readableProgress" class="readable-progress">{{ readableProgress }}</small></div>
    <div ref="list" class="side-live-list scheduler-live-list" tabindex="0"><span v-if="!entries.length" class="side-live-empty">暂无本软件运行状态</span><ol v-else><li v-for="(entry, index) in entries" :key="index" :class="'log-phase-' + entry.phase"><b class="log-phase">{{ phaseLabels[entry.phase] }}</b><span>{{ logLabel(entry.message) }}</span></li></ol><span v-if="summary" class="side-live-summary">{{ summary }}</span></div>
    <template v-if="runtime?.busy && hasScriptLog">
      <div class="side-live-header script-header"><b>{{ current ? `${current.task_name} · 脚本实时转译` : '脚本实时转译' }}</b></div>
      <div ref="scriptList" class="side-live-list script-live-list script-event-list" tabindex="0"><span v-if="scriptEvents.some(entry => entry.truncated)" class="script-truncated">实时转译缓冲已截断；请在历史详情查看脚本内部日志原文件。</span><span v-if="!scriptEvents.length" class="side-live-empty">等待脚本内部日志产生可转译事件。</span><ol v-else><li v-for="entry in scriptEvents" :key="entry.sequence" :class="'script-severity-' + entry.event.severity"><b class="script-severity">{{ levelLabel(entry) }}</b><span>{{ entry.event.message }}<small v-if="(entry.event.occurrence_count || 1) > 1" class="event-occurrences"> ×{{ entry.event.occurrence_count }}</small></span></li></ol></div>
    </template>
    </div>
  </section>
</template>

<style scoped>
.script-live-list { background: #10241f; border-color: #327d65; }
.side-live-toggle { display: none; }
.side-live-panel { display: flex; flex: 1; flex-direction: column; min-height: 0; }
@media (max-width: 1080px) {
  .side-live-log { height: auto; min-height: 0; }
  .side-live-toggle { display: block; width: 100%; padding: 7px 2px; font-size: 11px; }
  .side-live-panel { display: none; }
  .compact-open .side-live-panel { display: flex; position: fixed; z-index: 50; left: calc(var(--nav-width) + 8px); bottom: 12px; width: min(360px, calc(100vw - var(--nav-width) - 20px)); height: min(48vh, 400px); padding: 12px; border: 1px solid #345064; border-radius: 7px; background: #10202e; box-shadow: 0 8px 30px #000a; }
}
.readable-progress{display:block;margin-top:4px;color:#8ce0bf;font-size:12px;font-weight:400;line-height:1.35}
.has-script-log .script-live-list { flex: 2 1 0; }
.has-script-log .scheduler-live-list { flex: 1 1 0; }
.has-script-log .script-header { margin-top: 8px; }
.side-live-summary { display: block; color: #8ce0bf; padding-top: 6px; }
.script-truncated { display:block; margin-bottom:6px; color:#ffe098; font-size:12px; }
.event-occurrences{margin-left:4px;color:#9bc9d7;font-weight:700}
.script-event-list ol{display:grid;gap:6px;margin:0;padding:0;list-style:none}.script-event-list li{display:flex;align-items:flex-start;gap:6px;line-height:1.45}.script-severity{flex:none;min-width:36px;padding:1px 4px;border:1px solid #496577;border-radius:3px;background:#1a2b37;color:#b7cfdd;font-size:11px;text-align:center}.script-severity-info .script-severity{border-color:#287f8b;background:#123f45;color:#8cf1e6}.script-severity-warning .script-severity{border-color:#9a792e;background:#3a2d12;color:#ffe098}.script-severity-error .script-severity{border-color:#9d505b;background:#3a1e25;color:#ffb5bd}
.scheduler-live-list ol{display:grid;gap:6px;margin:0;padding-left:0;list-style:none}.scheduler-live-list li{display:flex;align-items:flex-start;gap:6px;line-height:1.45}.log-phase{flex:none;min-width:48px;padding:1px 4px;border:1px solid #496577;border-radius:3px;background:#1a2b37;color:#b7cfdd;font-size:11px;text-align:center}.log-phase-validation .log-phase{border-color:#3a7896;background:#123448;color:#9ce4ff}.log-phase-running .log-phase{border-color:#287f8b;background:#123f45;color:#8cf1e6}.log-phase-waiting .log-phase{border-color:#9a792e;background:#3a2d12;color:#ffe098}.log-phase-closing .log-phase{border-color:#8b658f;background:#32223a;color:#e7c0f1}.log-phase-cooling .log-phase{border-color:#6972a2;background:#242943;color:#c3ccff}.log-phase-success .log-phase{border-color:#398161;background:#123525;color:#9af0c1}.log-phase-failed .log-phase{border-color:#9d505b;background:#3a1e25;color:#ffb5bd}
</style>
