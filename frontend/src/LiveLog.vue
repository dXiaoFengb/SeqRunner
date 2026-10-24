<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { Runtime, ScriptLogSource } from './types'
import { logLabel } from './logLabels'

type Phase = 'validation' | 'running' | 'waiting' | 'closing' | 'cooling' | 'success' | 'failed' | 'info'
type LiveLogEntry = { message: string; phase: Phase }
type LegacyScriptLog = { runId: string; taskId: string; text: string }
const props = defineProps<{ logs: Array<string | LiveLogEntry>; runtime?: Runtime; scriptLogs?: ScriptLogSource[]; scriptLog?: LegacyScriptLog | null }>()
const phaseLabels: Record<Phase, string> = { validation: '校验', running: '运行', waiting: '等待确认', closing: '收尾', cooling: '冷却', success: '完成', failed: '失败', info: '信息' }
const entries = computed(() => props.logs.map((entry): LiveLogEntry => typeof entry === 'string' ? { message: entry, phase: 'info' } : entry))
const current = computed(() => { const run = props.runtime?.run; if (!run || !props.runtime?.busy) return null; return run.items.find(item => item.task_id === props.runtime?.activeTaskId && ['monitoring', 'waiting_manual'].includes(item.state)) || null })
const availableSources = computed(() => {
  const runId = props.runtime?.run?.id
  const taskId = current.value?.task_id
  const streams = (props.scriptLogs || []).filter((item) => item.runId === runId && item.taskId === taskId)
  const legacy = props.scriptLog
  if (!streams.length && legacy && legacy.runId === runId && legacy.taskId === taskId) return [{ ...legacy, sourceId: 'legacy', sourceName: '脚本日志', sourceType: 'external', sequence: 0, append: false, truncated: false, updatedAt: '' } as ScriptLogSource]
  return [...streams].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
})
const selectedSourceId = ref<string | null>(null)
const sourceScope = ref('')
const seenSourceSequences = ref<Record<string, number>>({})
const selectedSource = computed(() => availableSources.value.find((item) => item.sourceId === selectedSourceId.value) || availableSources.value[0] || null)
const hasScriptLog = computed(() => Boolean(current.value && (current.value.log_source_configured || current.value.ui_log_source_configured || availableSources.value.length)))
const sourceTypeLabel = (source: ScriptLogSource) => source.sourceType === 'ocr' ? '画面识别' : source.sourceType === 'window' ? '界面读取' : source.sourceName
const readableProgress = computed(() => props.runtime?.runCenter?.readableProgress || '')
const summary = computed(() => { const run = props.runtime?.run; if (!run || !['completed', 'stopped'].includes(run.state) || !run.started_at || !run.ended_at) return ''; const seconds = Math.max(0, Math.floor((Date.parse(run.ended_at) - Date.parse(run.started_at)) / 1000)); if (!Number.isFinite(seconds)) return ''; const state = run.state === 'stopped' ? '本轮已停止' : run.items.some(item => item.state === 'failed') ? '本轮完成，存在失败项' : '本轮执行完成'; return `${state}，总耗时 ${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分 ${seconds % 60} 秒` })
const list = ref<HTMLElement | null>(null)
const scriptList = ref<HTMLElement | null>(null)
function scrollToEnd(element: HTMLElement | null) { if (element) element.scrollTop = element.scrollHeight }
async function scrollSummaryToEnd() { await nextTick(); scrollToEnd(list.value) }
async function scrollScriptToEnd() { await nextTick(); scrollToEnd(scriptList.value) }
function sourceIsActive(source: ScriptLogSource) { return source.sourceId !== selectedSourceId.value && source.sequence > (seenSourceSequences.value[source.sourceId] ?? 0) }
function selectSource(source: ScriptLogSource) {
  selectedSourceId.value = source.sourceId
  seenSourceSequences.value = { ...seenSourceSequences.value, [source.sourceId]: source.sequence }
}
onMounted(async () => { await scrollSummaryToEnd(); await scrollScriptToEnd() })
watch(() => [props.logs, summary.value] as const, scrollSummaryToEnd, { flush: 'pre' })
watch(() => props.runtime?.run?.id, scrollSummaryToEnd)
watch(availableSources, (sources) => {
  const scope = `${props.runtime?.run?.id || ''}:${current.value?.task_id || ''}`
  if (scope !== sourceScope.value) {
    sourceScope.value = scope
    selectedSourceId.value = sources[0]?.sourceId || null
    seenSourceSequences.value = Object.fromEntries(sources.map((source) => [source.sourceId, source.sequence]))
    return
  }
  const available = new Set(sources.map((source) => source.sourceId))
  const retained = Object.fromEntries(Object.entries(seenSourceSequences.value).filter(([sourceId]) => available.has(sourceId)))
  if (!sources.some((item) => item.sourceId === selectedSourceId.value)) selectedSourceId.value = sources[0]?.sourceId || null
  const selected = sources.find((source) => source.sourceId === selectedSourceId.value)
  if (selected) retained[selected.sourceId] = selected.sequence
  seenSourceSequences.value = retained
}, { immediate: true })
watch(() => [selectedSource.value?.sequence, current.value?.task_id] as const, scrollScriptToEnd, { flush: 'pre' })
</script>

<template>
  <section class="side-live-log" :class="{ 'has-script-log': runtime?.busy && hasScriptLog }" aria-label="本轮日志摘要">
    <div class="side-live-header scheduler-header"><b>本轮日志摘要</b><small v-if="readableProgress" class="readable-progress">{{ readableProgress }}</small></div>
    <div ref="list" class="side-live-list scheduler-live-list" tabindex="0"><span v-if="!entries.length" class="side-live-empty">暂无实时日志</span><ol v-else><li v-for="(entry, index) in entries" :key="index" :class="'log-phase-' + entry.phase"><b class="log-phase">{{ phaseLabels[entry.phase] }}</b><span>{{ logLabel(entry.message) }}</span></li></ol><span v-if="summary" class="side-live-summary">{{ summary }}</span></div>
    <template v-if="runtime?.busy && hasScriptLog">
      <div class="side-live-header script-header"><b>{{ current ? `${current.task_name} · 脚本日志` : '脚本日志' }}</b></div>
      <div v-if="availableSources.length" class="script-source-tabs" aria-label="日志来源">
        <button v-for="source in availableSources" :key="source.sourceId" :class="{ active: selectedSource?.sourceId === source.sourceId }" :aria-pressed="selectedSource?.sourceId === source.sourceId" @click="selectSource(source)"><span>{{ sourceTypeLabel(source) }}</span><i v-if="sourceIsActive(source)" title="有新内容" /></button>
      </div>
      <div ref="scriptList" class="side-live-list script-live-list" tabindex="0"><span v-if="selectedSource?.truncated" class="script-truncated">实时缓冲已截断，可在历史页读取原文件。</span><span v-if="!selectedSource?.text" class="side-live-empty">{{ current ? (current.ui_log_status || '暂无法可靠获取该脚本的界面日志。') : '等待脚本运行' }}</span><pre v-else class="script-text">{{ selectedSource.text }}</pre></div>
    </template>
  </section>
</template>

<style scoped>
.script-live-list { background: #10241f; border-color: #327d65; }
.readable-progress{display:block;margin-top:4px;color:#8ce0bf;font-size:12px;font-weight:400;line-height:1.35}
.has-script-log .script-live-list { flex: 2 1 0; }
.has-script-log .scheduler-live-list { flex: 1 1 0; }
.has-script-log .script-header { margin-top: 8px; }
.side-live-summary { display: block; color: #8ce0bf; padding-top: 6px; }
.script-text { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; margin: 6px 0 0; }
.script-truncated { display:block; margin-bottom:6px; color:#ffe098; font-size:12px; }
.script-source-tabs { display:flex; gap:5px; overflow-x:auto; padding:5px 0 1px; }
.script-source-tabs button { position:relative; flex:0 0 auto; max-width:150px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; padding:3px 7px; color:#9ce4ff; border:1px solid #276282; background:#102a3a; border-radius:4px; font-size:12px; }
.script-source-tabs button.active { color:#fff; border-color:#3ba4e5; background:#1767a8; }
.script-source-tabs i { display:inline-block; width:6px; height:6px; margin-left:5px; border-radius:50%; background:#63e6be; vertical-align:middle; }
.scheduler-live-list ol{display:grid;gap:6px;margin:0;padding-left:0;list-style:none}.scheduler-live-list li{display:flex;align-items:flex-start;gap:6px;line-height:1.45}.log-phase{flex:none;min-width:48px;padding:1px 4px;border:1px solid #496577;border-radius:3px;background:#1a2b37;color:#b7cfdd;font-size:11px;text-align:center}.log-phase-validation .log-phase{border-color:#3a7896;background:#123448;color:#9ce4ff}.log-phase-running .log-phase{border-color:#287f8b;background:#123f45;color:#8cf1e6}.log-phase-waiting .log-phase{border-color:#9a792e;background:#3a2d12;color:#ffe098}.log-phase-closing .log-phase{border-color:#8b658f;background:#32223a;color:#e7c0f1}.log-phase-cooling .log-phase{border-color:#6972a2;background:#242943;color:#c3ccff}.log-phase-success .log-phase{border-color:#398161;background:#123525;color:#9af0c1}.log-phase-failed .log-phase{border-color:#9d505b;background:#3a1e25;color:#ffb5bd}
</style>
