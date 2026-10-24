<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { CalendarDays, FolderOpen, RotateCcw, Search } from '@lucide/vue'
import { VueMonacoEditor } from './monaco'
import type { RunItem, ReadableReport } from './types'
import { triggerLabel, stateLabel, sourceLabel, logLabel } from './logLabels'
import type { DetailEntry } from './detailLogLabels'

type HistoryRun = { id: string; queue_name: string; run_mode?: string; trigger_source: string; state: string; started_at: string; ended_at?: string | null; items: RunItem[]; scheduler_skips?: string[]; run_log_id?: string | null; run_log_status?: string | null; readable_report?: ReadableReport | null }
type Preset = 'today' | 'yesterday' | 'week' | 'month' | 'twoMonths' | 'threeMonths' | 'halfYear'
type Range = { start: string; end: string }
const props = defineProps<{ history: HistoryRun[]; selectedId: string | null; expanded: Set<string>; request?: (path: string, options?: RequestInit) => Promise<unknown> }>()
const emit = defineEmits<{ toggle: [string]; refresh: [] }>()
const presets: { key: Preset; label: string }[] = [{ key: 'today', label: '今天' }, { key: 'yesterday', label: '昨天' }, { key: 'week', label: '最近一周' }, { key: 'month', label: '最近一个月' }, { key: 'twoMonths', label: '最近两个月' }, { key: 'threeMonths', label: '最近三个月' }, { key: 'halfYear', label: '最近半年' }]
function dateKey(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function startMonthsBefore(date: Date, count: number) { const first = new Date(date.getFullYear(), date.getMonth() - count, 1); const last = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate(); first.setDate(Math.min(date.getDate(), last)); return first }
function rangeFor(preset: Preset): Range {
  const end = new Date(); end.setHours(12, 0, 0, 0)
  const start = new Date(end)
  if (preset === 'yesterday') { start.setDate(start.getDate() - 1); end.setDate(end.getDate() - 1) }
  else if (preset === 'week') start.setDate(start.getDate() - 6)
  else if (preset !== 'today') return { start: dateKey(startMonthsBefore(end, { month: 1, twoMonths: 2, threeMonths: 3, halfYear: 6 }[preset])), end: dateKey(end) }
  return { start: dateKey(start), end: dateKey(end) }
}
const initial = rangeFor('week')
const start = ref(initial.start), end = ref(initial.end), applied = ref<Range>({ ...initial })
const selected = ref<Preset | null>('week'), error = ref('')
const groupMode = ref<'day' | 'week' | 'month'>('day')
const keyword = ref('')
const triggerSource = ref<'all' | 'manual' | 'scheduled' | 'startup'>('all')
function selectPreset(key: Preset) { const range = rangeFor(key); start.value = range.start; end.value = range.end; selected.value = key; error.value = '' }
function editDates() { selected.value = null; error.value = '' }
function search() { if (!start.value || !end.value) { error.value = '请选择开始和结束日期。'; return }; if (start.value > end.value) { error.value = '开始日期不能晚于结束日期。'; return }; error.value = ''; taskDetailOpen.value = false; applied.value = { start: start.value, end: end.value } }
function reset() { groupMode.value = 'day'; keyword.value = ''; triggerSource.value = 'all'; selectPreset('week'); search() }
function recordDate(run: HistoryRun) { const date = new Date(run.started_at); return Number.isNaN(date.getTime()) ? '' : dateKey(date) }
function groupKey(day: string) {
  if (groupMode.value === 'day') return day
  if (groupMode.value === 'month') return day.slice(0, 7)
  const [year, month, date] = day.split('-').map(Number)
  const thursday = new Date(Date.UTC(year, month - 1, date))
  thursday.setUTCDate(thursday.getUTCDate() + 4 - (thursday.getUTCDay() || 7))
  const week = Math.ceil(((thursday.getTime() - Date.UTC(thursday.getUTCFullYear(), 0, 1)) / 86400000 + 1) / 7)
  return `${thursday.getUTCFullYear()}-W${String(week).padStart(2, '0')}`
}
function matchesRun(run: HistoryRun) {
  if (triggerSource.value !== 'all' && run.trigger_source !== triggerSource.value) return false
  const query = keyword.value.trim().toLocaleLowerCase()
  if (!query) return true
  const report = reportFor(run)
  const fields = [
    run.queue_name, run.state, run.trigger_source, ...(run.scheduler_skips || []),
    report?.conclusion, report?.completion_basis, ...(report?.risks || []), ...(report?.events || []).map(event => `${event.message} ${event.rule_id || ''}`),
    ...run.items.flatMap(item => [itemName(item), item.error_summary, item.completion_source, ...(item.log_summary || []), item.external_log_status, item.ui_log_status, item.detail_log_status, reportFor(run, item)?.conclusion, reportFor(run, item)?.completion_basis, ...(reportFor(run, item)?.risks || []), ...(reportFor(run, item)?.events || []).map(event => `${event.message} ${event.rule_id || ''}`)]),
  ]
  return fields.some(value => String(value || '').toLocaleLowerCase().includes(query))
}
const groups = computed(() => {
  const entries = new Map<string, HistoryRun[]>()
  for (const run of props.history) { const day = recordDate(run); if (!day || day < applied.value.start || day > applied.value.end || !matchesRun(run)) continue; const key = groupKey(day); const group = entries.get(key) || []; group.push(run); entries.set(key, group) }
  return [...entries.entries()].sort(([a], [b]) => b.localeCompare(a)).map(([date, runs]) => ({ date, runs: runs.sort((a, b) => new Date(b.started_at).getTime() - new Date(a.started_at).getTime()) }))
})
const selectedRunId = ref<string | null>(props.selectedId)
const selectedTaskId = ref<string | null>(null)
const taskDetailOpen = ref(false)
const lastTaskCard = ref<HTMLElement | null>(null)
const taskDetailPanel = ref<HTMLElement | null>(null)
const visibleRuns = computed(() => groups.value.flatMap((group) => group.runs))
const activeRun = computed(() => visibleRuns.value.find((run) => run.id === selectedRunId.value) || visibleRuns.value[0] || null)
const activeItem = computed(() => activeRun.value?.items.find((item) => item.task_id === selectedTaskId.value) || activeRun.value?.items[0] || null)
function selectRun(run: HistoryRun) { selectedRunId.value = run.id; selectedTaskId.value = null; taskDetailOpen.value = false }
function openTaskDetail(item: RunItem, event?: Event) {
  selectedTaskId.value = item.task_id
  taskDetailOpen.value = true
  lastTaskCard.value = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
  nextTick(() => taskDetailPanel.value?.focus())
}
function returnToRunReport() { taskDetailOpen.value = false; nextTick(() => lastTaskCard.value?.focus()) }
watch(() => [props.selectedId, props.history] as const, ([id]) => {
  if (!id) return
  const run = props.history.find((item) => item.id === id)
  if (!run) return
  const day = recordDate(run)
  if (day && (day < applied.value.start || day > applied.value.end)) { start.value = day; end.value = day; applied.value = { start: day, end: day }; selected.value = null; error.value = '' }
  selectedRunId.value = id
}, { immediate: true })
watch(visibleRuns, (runs) => {
  if (runs.length && !runs.some((run) => run.id === selectedRunId.value)) selectedRunId.value = runs[0].id
}, { immediate: true })
watch(activeRun, (run) => {
  if (!run) { selectedTaskId.value = null; taskDetailOpen.value = false; return }
  if (!run.items.some((item) => item.task_id === selectedTaskId.value)) selectedTaskId.value = run.items[0]?.task_id || null
})
function itemOutcome(item: RunItem) { const reason = item.error_summary || (item.exit_code !== null && item.exit_code !== undefined ? `退出码 ${item.exit_code}` : item.completion_source || '未提供摘要'); return item.state === 'failed' ? `失败：${reason}` : item.state === 'succeeded' ? `成功：${sourceLabel(item.completion_source || '已完成')}` : stateLabel(item.state) }
function itemName(item: RunItem) { return item.script_name && item.script_name !== item.task_name ? `${item.script_name} / ${item.task_name}` : item.task_name }
function uiLogStatus(item: RunItem) { return item.ui_log_status || (item.ui_log_id ? '已采集' : '暂无界面日志') }
function rawLogStatus(item: RunItem) { return item.detail_log_status || (item.detail_log_id ? '已采集' : '暂无原始日志') }
function runTime(run: HistoryRun) { const value = new Date(run.started_at); return Number.isNaN(value.getTime()) ? '时间未知' : value.toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' }) }
const detail = ref<{ runId: string; id: string | null; rawId: string | null; uiId: string | null; rawStatus: string; uiStatus: string; title: string; entries: DetailEntry[]; next: number | null; error: string; loading: boolean } | null>(null)
const rawDetail = ref(false)
const runDetail = ref(false)
type ExternalFile = { index: number; path: string; name: string; size?: number; modified_at?: number; available: boolean }
const external = ref<{ runId: string; taskId: string; files: ExternalFile[]; selected: ExternalFile | null; text: string; next: number | null; error: string; loading: boolean } | null>(null)
const displayedDetail = computed(() => (detail.value?.entries || []).map(entry => runDetail.value ? { ...entry, text: logLabel(entry.text) } : entry))
const fontSize = ref(13)
const stripBlankLines = ref(false)
function visibleLog(text: string) { return stripBlankLines.value ? text.split(/\r?\n/).filter(line => line.trim()).join('\n') : text }
const detailEditorText = computed(() => visibleLog(displayedDetail.value.map(entry => `[${new Date(entry.captured_at).toLocaleString('zh-CN', { hour12: false })}] ${entry.text}`).join('\n')))
const externalEditorText = computed(() => visibleLog(external.value?.text || ''))
const editorOptions = computed(() => ({ readOnly: true, fontSize: fontSize.value, fontFamily: 'Consolas, "Microsoft YaHei UI", monospace', lineHeight: Math.round(fontSize.value * 1.55), wordWrap: 'on' as const, scrollBeyondLastLine: false, minimap: { enabled: false }, automaticLayout: true, scrollbar: { vertical: 'auto' as const, horizontal: 'auto' as const, verticalScrollbarSize: 9, horizontalScrollbarSize: 9 }, find: { addExtraSpaceOnTop: false } }))
function registerLogLanguage(instance: any) {
  if (instance.languages.getLanguages().some((language: { id: string }) => language.id === 'runner-logfile')) return
  instance.languages.register({ id: 'runner-logfile' })
  instance.languages.setMonarchTokensProvider('runner-logfile', { tokenizer: { root: [[/\[\d{4}-\d\d-\d\d[^\]]*\]/, 'timestamp'], [/\[(?:ERR|ERROR)\]/, 'error'], [/\[(?:WRN|WARN|WARNING)\]/, 'warning'], [/\[(?:INF|INFO)\]/, 'info'], [/https?:\/\/[^\s]+/, 'url'], [/[A-Za-z]:\\[^\s]+/, 'path'], [/\{.*\}|\[.*\]/, 'json'], [/\[[A-Za-z][\w.-]*\]/, 'module']] } })
  instance.editor.defineTheme('runner-log-dark', { base: 'vs-dark', inherit: true, rules: [{ token: 'timestamp', foreground: '55c8ff' }, { token: 'error', foreground: 'ff7884' }, { token: 'warning', foreground: 'f6c85f' }, { token: 'info', foreground: '5eead4' }, { token: 'module', foreground: 'd3a7ff' }, { token: 'path', foreground: 'a5d6ff' }, { token: 'url', foreground: '83bfff' }, { token: 'json', foreground: 'd7e4ec' }], colors: { 'editor.background': '#091620', 'editorLineNumber.foreground': '#537083', 'editorLineNumber.activeForeground': '#a9c1cf' } })
}
async function loadDetail(run: HistoryRun, item: RunItem) {
  runDetail.value = false
  rawDetail.value = false
  detail.value = { runId: run.id, id: item.ui_log_id || null, uiId: item.ui_log_id || null, rawId: item.detail_log_id || null, rawStatus: item.detail_log_status || '暂无原始日志', uiStatus: item.ui_log_status || '暂无法可靠获取该脚本的界面日志', title: item.task_name, entries: [], next: item.ui_log_id ? 0 : null, error: item.ui_log_id ? '' : (item.ui_log_status || '暂无法可靠获取该脚本的界面日志'), loading: false }
  await moreDetail()
}
async function loadRunDetail(run: HistoryRun) {
  runDetail.value = true; rawDetail.value = false
  const id = run.run_log_id || null
  detail.value = { runId: run.id, id, uiId: id, rawId: null, rawStatus: '', uiStatus: run.run_log_status || (id ? '本软件本轮运行日志' : '旧记录没有保存完整的本轮运行日志'), title: '本软件本轮运行', entries: [], next: id ? 0 : null, error: id ? '' : '旧记录没有保存完整的本轮运行日志', loading: false }
  await moreDetail()
}
async function toggleRawDetail() {
  if (!detail.value) return
  rawDetail.value = !rawDetail.value
  const current = detail.value
  current.id = rawDetail.value ? current.rawId : current.uiId
  current.entries = []; current.next = current.id ? 0 : null; current.error = current.id ? '' : (rawDetail.value ? current.rawStatus : current.uiStatus); current.loading = false
  await moreDetail()
}
async function moreDetail() {
  const current = detail.value
  if (!current || !current.id || current.next === null || !props.request || current.loading) return
  current.loading = true
  const requestedId = current.id
  try {
    const data = await props.request(`/api/history/${encodeURIComponent(current.runId)}/details/${encodeURIComponent(current.id)}?offset=${current.next}`) as { entries: DetailEntry[]; next_offset: number | null }
    if (detail.value === current && current.id === requestedId) { current.entries.push(...data.entries); current.next = data.next_offset }
  } catch (error) { if (detail.value === current && current.id === requestedId) { current.error = `无法读取详细日志：${error instanceof Error ? error.message : String(error)}`; current.next = null } }
  finally { if (detail.value === current && current.id === requestedId) current.loading = false }
}
async function openExternalLogs(run: HistoryRun, item: RunItem) {
  if (!props.request) return
  external.value = { runId: run.id, taskId: item.task_id, files: [], selected: null, text: '', next: null, error: '', loading: true }
  try {
    const result = await props.request(`/api/history/${encodeURIComponent(run.id)}/external-logs/${encodeURIComponent(item.task_id)}`) as { files: ExternalFile[]; status?: string }
    if (!external.value) return
    external.value.files = result.files
    external.value.error = result.files.length ? '' : (result.status || '本轮没有可用的外部日志文件。')
    if (result.files[0]?.available) await selectExternal(result.files[0])
  } catch (error) { if (external.value) external.value.error = `无法读取外部日志：${String(error)}` }
  finally { if (external.value) external.value.loading = false }
}
async function selectExternal(file: ExternalFile) {
  const current = external.value
  if (!current || !props.request || !file.available) return
  current.selected = file; current.text = ''; current.next = 0; current.error = ''; current.loading = true
  try {
    const result = await props.request(`/api/history/${encodeURIComponent(current.runId)}/external-logs/${encodeURIComponent(current.taskId)}/${file.index}?offset=0`) as { text: string; next_offset: number | null }
    if (external.value === current) { current.text = result.text; current.next = result.next_offset }
  } catch (error) { if (external.value === current) current.error = `外部日志不可读取：${String(error)}` }
  finally { if (external.value === current) current.loading = false }
}
async function loadMoreExternal() {
  const current = external.value
  if (!current || !props.request || current.next === null || current.loading) return
  current.loading = true
  try {
    const result = await props.request(`/api/history/${encodeURIComponent(current.runId)}/external-logs/${encodeURIComponent(current.taskId)}/${current.selected?.index}?offset=${current.next}`) as { text: string; next_offset: number | null }
    if (external.value === current) { current.text += result.text; current.next = result.next_offset }
  } catch (error) { if (external.value === current) current.error = `无法继续读取外部日志：${String(error)}` }
  finally { if (external.value === current) current.loading = false }
}
async function copyExternal() { if (!external.value?.text) return; try { await window.desktopRunner?.copyText?.(external.value.text) } catch (error) { external.value.error = `复制失败：${String(error)}` } }
function externalReference(current: NonNullable<typeof external.value>) { return { runId: current.runId, taskId: current.taskId, index: current.selected!.index } }
async function exportExternal() { const current = external.value; if (!current?.selected) return; try { await window.desktopRunner?.exportFile?.(externalReference(current)) } catch (error) { current.error = `导出失败：${String(error)}` } }
async function showExternalFolder() { const current = external.value; if (!current?.selected) return; try { await window.desktopRunner?.showInFolder?.(externalReference(current)) } catch (error) { current.error = `无法打开文件夹：${String(error)}` } }
const cleanupDate = ref(''), cleanupCount = ref<number | null>(null), cleanupError = ref('')
const cleanupIds = ref<string[]>([])
async function previewCleanup() {
  cleanupCount.value = null; cleanupError.value = ''
  if (!cleanupDate.value || !props.request) { cleanupError.value = '请选择清理截止日期。'; return }
  try { const result = await props.request('/api/history/detail-cleanup/preview', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ cutoff: cleanupDate.value }) }) as { count: number; ids: string[] }; cleanupCount.value = result.count; cleanupIds.value = result.ids }
  catch (error) { cleanupError.value = `无法预览清理：${String(error)}` }
}
async function confirmCleanup() {
  if (cleanupCount.value === null || !props.request) return
  try { const result = await props.request('/api/history/detail-cleanup', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ cutoff: cleanupDate.value, ids: cleanupIds.value }) }) as { count: number }; cleanupCount.value = null; cleanupError.value = `已清理 ${result.count} 条详细日志。`; detail.value = null; emit('refresh') }
  catch (error) { cleanupCount.value = null; cleanupError.value = `清理未完成：${String(error)}`; emit('refresh') }
}
function runHasFailures(run: HistoryRun) { return run.items.some((item) => item.state === 'failed') }
function runOutcome(run: HistoryRun) { if (run.state === 'stopped') return '已停止'; if (run.state !== 'completed') return stateLabel(run.state); if (runHasFailures(run)) return '已完成（存在失败）'; return run.items.length && run.items.every(item => item.state === 'succeeded') ? '已完成（全部成功）' : '已完成（存在未执行项）' }
function reportFor(run: HistoryRun, item?: RunItem) { return item?.readable_report || run.readable_report || null }
function durationLabel(seconds: number | null | undefined) { if (seconds === null || seconds === undefined) return ''; return `${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分 ${seconds % 60} 秒` }
function itemHasRisk(run: HistoryRun, item: RunItem) { const report = reportFor(run, item); return item.state === 'failed' || Boolean(report?.risk_count || report?.risks.length || item.unrecognized_log_count) }
function eventLevel(event: NonNullable<ReadableReport>['events'][number]) { return event.severity === 'error' ? 'ERROR' : event.severity === 'warning' ? 'WARN' : 'INFO' }
function readableEventText(run: HistoryRun, item: RunItem) {
  const report = reportFor(run, item)
  if (!report?.events.length) return '此历史项没有保存可读事件流。\n\n该区域只展示本软件根据已确认运行事实生成的事件，不是脚本原文，也不会参与成功、失败或停止的终态判断。'
  return report.events.map((event, index) => {
    const time = new Date(event.timestamp)
    const timestamp = Number.isNaN(time.getTime()) ? event.timestamp : time.toLocaleString('zh-CN', { hour12: false })
    return `${String(index + 1).padStart(4, ' ')}  [${timestamp}] [${eventLevel(event)}] [${event.phase}] [${itemName(item)}]${event.rule_id ? ` [${event.rule_id}]` : ''} ${event.message}`
  }).join('\n')
}
</script>

<template>
  <section class="log-page" aria-label="日志">
    <div class="log-filters">
      <div class="filter-label">快捷选择</div>
      <div class="quick-ranges"><button v-for="preset in presets" :key="preset.key" type="button" :class="{ primary: selected === preset.key }" :aria-pressed="selected === preset.key" @click="selectPreset(preset.key)">{{ preset.label }}</button></div>
      <div class="filter-row">
        <label class="group-mode">合并模式<select v-model="groupMode" aria-label="合并模式"><option value="day">按日合并</option><option value="week">按周合并</option><option value="month">按月合并</option></select></label>
        <label>开始日期<input v-model="start" type="date" aria-label="开始日期" @input="editDates"></label>
        <label>结束日期<input v-model="end" type="date" aria-label="结束日期" @input="editDates"></label>
        <label>关键词<input v-model="keyword" type="search" aria-label="关键词" placeholder="名称、错误、摘要或跳过原因"></label>
        <label>触发来源<select v-model="triggerSource" aria-label="触发来源"><option value="all">全部触发来源</option><option value="manual">手动</option><option value="scheduled">定时</option><option value="startup">启动时</option></select></label>
        <div class="cleanup"><label>清理明细截止日期<input v-model="cleanupDate" type="date" aria-label="清理明细截止日期" @input="cleanupCount = null"></label><button type="button" @click="previewCleanup">清理详细日志</button></div>
        <div class="filter-actions"><button class="primary" type="button" @click="search"><Search aria-hidden="true" />搜索</button><button type="button" @click="reset"><RotateCcw aria-hidden="true" />重置</button></div>
      </div>
      <p v-if="error" class="filter-error" role="alert">{{ error }}</p>
      <p v-if="cleanupError" role="status">{{ cleanupError }}</p>
      <div v-if="cleanupCount !== null" class="cleanup-confirm" role="alertdialog" aria-label="确认清理详细日志"><p>确定清理 {{ cleanupDate }} 当天及之前的 {{ cleanupCount }} 条详细日志？运行记录与原始软件日志会保留。</p><button class="danger" @click="confirmCleanup">确认清理</button><button @click="cleanupCount = null">取消</button></div>
    </div>
    <div v-if="!groups.length" class="log-empty"><FolderOpen aria-hidden="true" /><strong>{{ history.length ? '没有符合条件的日志' : '暂无运行记录' }}</strong><span>{{ history.length ? '请调整日期范围后重新搜索。' : '运行完成后，记录会显示在这里。' }}</span></div>
    <section v-if="groups.length" class="history-workspace">
      <aside class="history-navigator" aria-label="运行记录导航">
        <section v-for="group in groups" :key="group.date" class="log-group"><h2><CalendarDays aria-hidden="true" />{{ group.date }}</h2><button v-for="run in group.runs" :key="run.id" type="button" class="run-record" :class="{ selected: activeRun?.id === run.id, failed: runHasFailures(run) }" @click="selectRun(run)"><b>{{ run.run_mode === 'plan' ? `计划：${run.queue_name}` : run.run_mode === 'all_scripts' ? '全部软件脚本' : run.queue_name }}</b><small>{{ groupMode === 'day' ? '' : recordDate(run) + ' ' }}{{ runTime(run) }} · {{ triggerLabel(run.trigger_source) }}</small><span class="record-outcome">{{ runOutcome(run) }}</span><span v-if="reportFor(run)" class="run-conclusion">{{ reportFor(run)?.conclusion }}</span><span v-else class="legacy-report">功能启用前保存的历史项</span></button></section>
      </aside>
      <article v-if="activeRun" class="history-report-panel" aria-label="运行报告">
        <header class="run-record-head"><div><b>{{ activeRun.run_mode === 'plan' ? `计划：${activeRun.queue_name}` : activeRun.run_mode === 'all_scripts' ? '全部软件脚本' : activeRun.queue_name }}</b><small>{{ recordDate(activeRun) }} {{ runTime(activeRun) }} · {{ triggerLabel(activeRun.trigger_source) }} · {{ runOutcome(activeRun) }}</small><p v-if="reportFor(activeRun)" class="run-conclusion">{{ reportFor(activeRun)?.conclusion }}<span v-if="reportFor(activeRun)?.duration_seconds !== null && reportFor(activeRun)?.duration_seconds !== undefined"> · {{ durationLabel(reportFor(activeRun)?.duration_seconds) }}</span></p><small v-else class="legacy-report">此记录生成于可读报告功能启用前。</small></div><button type="button" @click="loadRunDetail(activeRun)">查看本轮运行日志</button></header>
        <section v-if="!taskDetailOpen" class="run-report run-report-overview">
          <header><b>运行报告</b><span>{{ reportFor(activeRun)?.completion_basis || '该历史项未保存可读报告' }}</span></header>
          <p v-for="risk in reportFor(activeRun)?.risks || []" :key="risk" class="report-risk">{{ risk }}</p>
          <p class="report-guidance">选择一个脚本查看本软件生成的可读事件流、界面采集状态和原文入口。脚本内部日志不会在此处直接展开。</p>
          <div class="task-tabs" aria-label="脚本详情选择">
            <button v-for="item in activeRun.items" :key="item.task_id" type="button" class="task-tab" :class="{ selected: selectedTaskId === item.task_id, failed: item.state === 'failed', stopped: item.state === 'stopped', risk: itemHasRisk(activeRun, item) }" :aria-label="`查看 ${itemName(item)} 的脚本详情`" @click="openTaskDetail(item, $event)">
              <span class="task-tab-state">{{ item.state === 'failed' ? '失败' : item.state === 'stopped' ? '已停止' : item.state === 'succeeded' ? '成功' : stateLabel(item.state) }}</span>
              <span class="task-tab-main"><b>{{ itemName(item) }}</b><small>{{ reportFor(activeRun, item)?.conclusion || itemOutcome(item) }}</small></span>
              <span class="task-tab-meta"><span v-if="itemHasRisk(activeRun, item)" class="task-tab-risk">存在需查看项</span><span class="task-tab-action">查看详情</span></span>
            </button>
          </div>
        </section>
        <section v-else-if="activeItem" ref="taskDetailPanel" class="task-detail-panel" tabindex="-1" aria-label="脚本详情">
          <header class="task-detail-head"><div><button type="button" class="text-button" @click="returnToRunReport">← 返回运行报告</button><h2>{{ itemName(activeItem) }}</h2><p>{{ reportFor(activeRun, activeItem)?.conclusion || itemOutcome(activeItem) }}</p></div><div class="task-detail-actions"><button v-if="activeItem.external_log_files?.length" type="button" class="primary" @click="openExternalLogs(activeRun, activeItem)">查看脚本内部日志</button><button type="button" @click="loadDetail(activeRun, activeItem)">查看界面采集日志</button></div></header>
          <section class="task-facts" aria-label="脚本运行事实"><div><span>最终状态</span><b>{{ itemOutcome(activeItem) }}</b></div><div><span>完成依据</span><b>{{ reportFor(activeRun, activeItem)?.completion_basis || '未保存可读报告' }}</b></div><div><span>耗时</span><b>{{ durationLabel(reportFor(activeRun, activeItem)?.duration_seconds) || '未记录' }}</b></div><div><span>来源状态</span><b>{{ activeItem.external_log_status || rawLogStatus(activeItem) }}</b></div></section>
          <p v-for="risk in reportFor(activeRun, activeItem)?.risks || []" :key="risk" class="report-risk">{{ risk }}</p>
          <section class="derived-events"><header><div><h3>本软件事件流</h3><p>根据已确认运行事实生成，不是脚本原文，不参与终态仲裁。</p></div><span>{{ reportFor(activeRun, activeItem)?.generator_version || '旧记录' }}</span></header><div class="monaco-host derived-event-editor"><VueMonacoEditor :value="visibleLog(readableEventText(activeRun, activeItem))" language="runner-logfile" theme="runner-log-dark" :options="editorOptions" height="100%" @before-mount="registerLogLanguage" /></div></section>
          <section class="task-source-status"><h3>日志来源与历史入口</h3><p>界面采集：{{ uiLogStatus(activeItem) }}</p><p>旧本地详细日志：{{ rawLogStatus(activeItem) }}</p><p v-if="activeItem.external_log_status">脚本内部日志：{{ activeItem.external_log_status }}</p><p v-if="activeItem.log_summary?.length">本轮摘要：{{ activeItem.log_summary.map(logLabel).join('；') }}</p><p v-if="activeItem.unrecognized_log_count">未识别的外部原始记录：{{ activeItem.unrecognized_log_count }} 条（仅统计，不解释原文）</p></section>
        </section>
        <p v-if="activeRun.scheduler_skips?.length" class="schedule-skips"><b>计划跳过</b><span v-for="skip in activeRun.scheduler_skips" :key="skip">{{ skip }}</span></p>
      </article>
    </section>
    <div v-if="detail" class="detail-overlay" @click.self="detail = null"><section class="detail-dialog log-reader-dialog" role="dialog" aria-modal="true" aria-label="详细日志"><header><div><h2>{{ detail.title }} · 详细日志</h2><p>{{ rawDetail ? '显示采集时间；日志原文中的时间保持不变。' : detail.uiStatus }}</p></div><div class="reader-tools"><label>字号<select v-model.number="fontSize" aria-label="日志字号"><option :value="12">12 px</option><option :value="13">13 px</option><option :value="14">14 px</option><option :value="16">16 px</option></select></label><label><input v-model="stripBlankLines" type="checkbox"> 隐藏空行</label><button v-if="!runDetail" type="button" @click="toggleRawDetail">{{ rawDetail ? '返回脚本界面日志' : '查看原始日志' }}</button><button @click="detail = null">关闭</button></div></header><div class="detail-content monaco-host"><p v-if="detail.error" role="alert">{{ detail.error }}</p><p v-else-if="!detail.entries.length && !detail.loading">暂无详细日志。</p><VueMonacoEditor v-else :value="detailEditorText" language="runner-logfile" theme="runner-log-dark" :options="editorOptions" height="100%" @before-mount="registerLogLanguage" /></div><div class="detail-buttons external-actions"><span class="reader-range">当前已读取 {{ detail.entries.length }} 条记录</span><button v-if="detail.next !== null" :disabled="detail.loading" @click="moreDetail">{{ detail.loading ? '正在读取' : '加载更多' }}</button></div></section></div>
    <div v-if="external" class="detail-overlay" @click.self="external = null"><section class="detail-dialog log-reader-dialog" role="dialog" aria-modal="true" aria-label="脚本内部日志"><header class="external-log-header"><div><h2>脚本内部日志</h2><p>直接读取脚本内部日志原文件；本软件不会保存副本。</p></div><div class="external-header-actions"><div class="external-files" aria-label="选择脚本内部日志文件"><button v-for="file in external.files" :key="file.index" :disabled="!file.available" :class="{ primary: external.selected?.index === file.index }" @click="selectExternal(file)">{{ file.name }}{{ file.available ? '' : '（不可读取）' }}</button></div><button @click="external = null">关闭</button></div></header><div class="reader-tools"><label>字号<select v-model.number="fontSize" aria-label="日志字号"><option :value="12">12 px</option><option :value="13">13 px</option><option :value="14">14 px</option><option :value="16">16 px</option></select></label><label><input v-model="stripBlankLines" type="checkbox"> 隐藏空行</label></div><div class="detail-content monaco-host"><p v-if="external.error" role="alert">{{ external.error }}</p><p v-else-if="!external.text && !external.loading">暂无内容。</p><VueMonacoEditor v-else :value="externalEditorText" language="runner-logfile" theme="runner-log-dark" :options="editorOptions" height="100%" @before-mount="registerLogLanguage" /></div><div class="detail-buttons external-actions"><span class="reader-range">{{ external.next === null ? '已读取完整文件' : '当前仅搜索已读取内容' }}</span><button v-if="external.next !== null" :disabled="external.loading" @click="loadMoreExternal">{{ external.loading ? '读取中…' : '加载更多' }}</button><button :disabled="!external.selected || !external.text || external.text.length > 10 * 1024 * 1024" @click="copyExternal">复制当前已读取内容</button><button :disabled="!external.selected" @click="exportExternal">导出原文件</button><button :disabled="!external.selected" @click="showExternalFolder">打开所在文件夹</button></div></section></div>
  </section>
</template>

<style scoped>
.log-page{padding-bottom:48px}.log-filters,.live-log{padding:24px;border:1px solid var(--line);border-radius:8px;background:var(--panel);box-shadow:inset 0 1px rgba(255,255,255,.04)}.filter-label,.filter-row label,.group-mode>span{color:var(--muted);font-size:14px;font-weight:600}.quick-ranges{display:flex;flex-wrap:wrap;gap:9px;margin:12px 0 20px}.filter-row{display:flex;align-items:flex-end;flex-wrap:wrap;gap:16px;border-top:1px solid var(--line);padding-top:20px}.filter-row label,.group-mode{display:grid;gap:7px}.filter-row input{min-width:180px;min-height:38px;padding:8px 10px;border:1px solid var(--line-strong);border-radius:6px;background:var(--panel-deep);color:var(--ink);color-scheme:dark}.filter-row input:focus-visible{border-color:var(--teal);outline:2px solid rgba(22,119,255,.3)}.group-mode strong{display:flex;align-items:center;min-height:38px;padding:0 12px;border:1px solid var(--line-strong);border-radius:6px;color:var(--ink);font-size:14px}.filter-actions{display:flex;gap:9px;margin-left:auto}.filter-actions button{display:inline-flex;align-items:center;gap:7px}.filter-actions svg,.log-group svg{width:16px;height:16px}.filter-error{margin:12px 0 0;color:#f5a2a8}.log-empty{min-height:250px;display:grid;place-content:center;justify-items:center;gap:9px;text-align:center;color:var(--muted)}.log-empty svg{width:42px;height:42px;opacity:.7}.log-empty strong{font-size:18px;color:var(--ink)}.log-group{margin-top:24px}.log-group h2,.live-log h2{display:flex;align-items:center;gap:8px;font-size:18px;margin:0;color:var(--ink)}.live-log{margin-top:24px}.live-log p{color:var(--muted);margin:12px 0 0}.logs{margin-bottom:0}@media(max-width:980px){.filter-actions{margin-left:0}}@media(max-width:620px){.log-filters,.live-log{padding:16px}.filter-row label{width:100%}.filter-row input{width:100%}}
</style>

<style scoped>
.cleanup{display:flex;align-items:end;gap:8px;flex-wrap:wrap;margin-top:0}.cleanup label{display:grid;gap:7px;color:var(--muted);font-size:14px}.cleanup input{min-height:38px;padding:8px;border:1px solid var(--line-strong);border-radius:6px;color:var(--ink);background:var(--panel-deep);color-scheme:dark}.cleanup-confirm{margin-top:12px;padding:12px;border:1px solid #80602d;border-radius:6px;background:#292316}.cleanup-confirm p{margin:0 0 10px}.cleanup-confirm button+button{margin-left:8px}.detail-overlay{position:fixed;inset:0;z-index:2000;display:grid;place-items:center;padding:24px;background:#000a}.detail-dialog{width:min(100%,1160px);max-height:90vh;display:flex;flex-direction:column;gap:12px;padding:20px;background:var(--panel);border:1px solid var(--line-strong);border-radius:8px}.detail-dialog header{display:flex;justify-content:space-between;align-items:center;gap:12px}.detail-dialog h2{font-size:18px}.detail-dialog p{margin:0;color:var(--muted)}.detail-content{overflow:auto;min-height:140px;padding:12px;background:#0a1722}.monaco-host{height:min(62vh,720px);padding:0;border:1px solid #203c4e}.run-record li button{justify-self:start}.external-log-header{align-items:flex-start!important}.external-header-actions{display:flex;align-items:center;justify-content:flex-end;gap:8px;min-width:0}.external-files{display:flex;justify-content:flex-end;gap:7px;max-width:620px;overflow-x:auto;padding-bottom:2px}.external-files button{flex:none;white-space:nowrap}.external-actions{display:flex;align-items:center;justify-content:flex-end;gap:8px;flex-wrap:wrap}.reader-tools{display:flex;justify-content:flex-end;align-items:center;gap:12px;color:var(--muted);font-size:12px}.reader-tools label{display:flex;align-items:center;gap:5px}.reader-tools select{min-height:30px;background:var(--panel-deep);color:var(--ink);border:1px solid var(--line-strong);border-radius:4px}.reader-range{margin-right:auto;color:#84a9bc;font-size:12px}.run-conclusion{margin:4px 0 0;color:#9ee5c3;font-size:13px}.legacy-report{color:#9aafbd!important}.run-report{display:grid;gap:10px;margin-top:14px;padding:12px;border:1px solid #2b5267;background:#0d1c28}.run-report>header{display:flex;justify-content:space-between;gap:12px;color:#d2e5ef}.run-report>header span{color:#83a8b9;font-size:12px}.report-risk{margin:0;padding:7px 9px;border-left:3px solid #c58a45;background:#2c2417;color:#ffe0a4;font-size:13px}.report-event-warning{color:#ffe098!important}.report-event-error{color:#ffb5bd!important}@media(max-width:760px){.external-log-header{align-items:stretch!important;flex-direction:column}.external-header-actions{justify-content:space-between}.external-files{justify-content:flex-start;max-width:none}.external-actions{justify-content:stretch}.external-actions button{flex:1 1 auto}.detail-dialog{padding:14px}.detail-dialog header{align-items:flex-start;flex-direction:column}.reader-tools{justify-content:flex-start;flex-wrap:wrap}.monaco-host{height:52vh}}
</style>

<style scoped>
.log-filters{padding:18px}.quick-ranges{gap:7px;margin:8px 0 14px}.quick-ranges button{padding:6px 10px;font-size:14px}.filter-row{gap:10px;padding-top:14px}.filter-row input,.group-mode select{min-width:150px;min-height:36px;padding:6px 8px;border:1px solid var(--line-strong);border-radius:6px;background:var(--panel-deep);color:var(--ink);color-scheme:dark}.group-mode select{min-width:110px}.cleanup input{min-width:150px}.cleanup button,.filter-actions button{min-height:36px;padding:6px 10px;font-size:14px}@media(max-width:980px){.filter-actions{margin-left:0}}
.run-record li{display:flex;align-items:center;justify-content:space-between;gap:12px}.history-item-info{display:grid;min-width:0;gap:3px;overflow-wrap:anywhere}.run-record li button{flex:none}@media(max-width:620px){.run-record li{align-items:flex-start;flex-direction:column}}
</style>

<style scoped>
.history-workspace{display:grid;grid-template-columns:minmax(260px,360px) minmax(0,1fr);gap:18px;margin-top:24px;align-items:start}.history-navigator{min-width:0;max-height:calc(100vh - 250px);overflow:auto;padding:0 3px 8px}.history-navigator .log-group{margin-top:0}.history-navigator .log-group+.log-group{margin-top:18px}.history-navigator .log-group h2{font-size:15px;margin:0 0 8px}.history-navigator .run-record{display:grid;width:100%;gap:4px;margin:8px 0 0;text-align:left;cursor:pointer}.history-navigator .run-record:hover{border-color:#477995}.history-navigator .run-record b{color:var(--ink)}.history-navigator .run-record small{color:#77c5bc}.record-outcome{font-size:12px;color:#b9cad4}.history-navigator .run-record.failed .record-outcome{color:#ffb5bd}.history-report-panel{min-width:0;padding:18px;border:1px solid var(--line);border-radius:8px;background:var(--panel);box-shadow:inset 0 1px rgba(255,255,255,.04)}.history-report-panel>.run-record-head{align-items:flex-start}.history-report-panel>.run-record-head>div{display:grid;gap:4px}.history-report-panel>.run-record-head small{color:#73c9c0}.history-report-panel>.run-record-head>button{flex:none}.history-report-panel .run-report{margin-top:18px}.history-report-panel .run-report ul{display:grid;gap:9px;margin:14px 0 0;padding:0;list-style:none}.history-report-panel .run-report li{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px;border-left:2px solid #355567;background:#101f2d;font-size:13px}.history-report-panel .run-report li span{color:#b8cbd7}.history-report-panel .run-report li small{color:#8ab4c3}.history-report-panel .run-report .history-item-failed{border-left-color:#db6d69;background:#2a1d25;color:#ffe0dc}@media(max-width:980px){.history-workspace{grid-template-columns:1fr}.history-navigator{display:flex;gap:12px;max-height:none;overflow:visible;padding:0}.history-navigator .log-group{flex:1;min-width:0}.history-navigator .run-record{min-height:0}.history-report-panel{min-width:0}}@media(max-width:620px){.history-navigator{display:block}.history-navigator .log-group+.log-group{margin-top:18px}.history-report-panel{padding:14px}.history-report-panel .run-report li{align-items:flex-start;flex-direction:column}}
</style>

<style scoped>
.report-guidance{margin:0;color:#9ab4c2;font-size:13px}.task-tabs{display:grid;grid-template-columns:minmax(0,1fr);gap:8px;max-block-size:456px;overflow-y:auto;overflow-x:hidden;padding:3px 4px 8px 1px;scrollbar-color:#38627c transparent}.task-tab{position:relative;display:grid;grid-template-columns:74px minmax(0,1fr) auto;align-items:center;gap:13px;min-height:78px;width:100%;padding:11px 15px 11px 12px;border:1px solid #31576b;border-left:4px solid #4eb5d2;border-radius:7px;background:#102434;color:var(--ink);text-align:left;cursor:pointer;transition:border-color .16s ease,background .16s ease;overflow-wrap:anywhere}.task-tab:hover{border-color:#65cce7;background:#133044}.task-tab.selected{border-color:#62d8ed;background:#153549;box-shadow:inset 0 0 0 1px rgba(101,219,239,.45)}.task-tab:focus-visible{outline:2px solid #64d5e8;outline-offset:2px}.task-tab.failed{border-left-color:#ef7676;background:#2c1d26}.task-tab.stopped{border-left-color:#bc91d4;background:#271e31}.task-tab.risk:not(.failed){box-shadow:inset 0 0 0 1px rgba(218,169,76,.35)}.task-tab.selected.risk:not(.failed){box-shadow:inset 0 0 0 1px #62d8ed}.task-tab-state{width:max-content;padding:2px 6px;border:1px solid #3d8ea8;border-radius:4px;color:#8ee8ee;font-size:12px}.task-tab.failed .task-tab-state{border-color:#ba6268;color:#ffb4bc}.task-tab-main{display:grid;gap:4px;min-width:0}.task-tab b{font-size:16px}.task-tab small{color:#b8ccd8;line-height:1.4}.task-tab-meta{display:grid;justify-items:end;gap:5px;min-width:88px}.task-tab-risk{color:#ffd783;font-size:12px}.task-tab-action{color:#70d7ef;font-size:12px;font-weight:700}.task-detail-panel{display:grid;gap:16px;margin-top:18px;padding:18px;border:1px solid #356276;border-radius:8px;background:#0b1b27;outline:none}.task-detail-panel:focus-visible{box-shadow:0 0 0 2px rgba(100,213,232,.55)}.task-detail-head{display:flex;justify-content:space-between;gap:16px;border-bottom:1px solid #27465a;padding-bottom:14px}.task-detail-head>div:first-child{min-width:0}.task-detail-head h2{margin:8px 0 4px;font-size:22px}.task-detail-head p{margin:0;color:#8fe4d5}.task-detail-actions{display:flex;align-items:start;justify-content:flex-end;gap:8px;flex-wrap:wrap}.text-button{padding:0;border:0;background:transparent;color:#76cce3;text-decoration:underline;text-underline-offset:3px}.task-facts{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:9px}.task-facts div{display:grid;gap:5px;min-width:0;padding:10px;border:1px solid #29485b;border-radius:6px;background:#0d2534}.task-facts span{color:#88adbd;font-size:12px}.task-facts b{color:#d7ecf3;font-size:13px;overflow-wrap:anywhere}.derived-events{display:grid;gap:10px}.derived-events header{display:flex;align-items:start;justify-content:space-between;gap:12px}.derived-events h3,.task-source-status h3{margin:0;color:#dcecf2;font-size:16px}.derived-events p,.task-source-status p{margin:5px 0 0;color:#99b9c7;font-size:13px}.derived-events header>span{flex:none;color:#79c7d8;font-size:12px}.derived-event-editor{height:min(48vh,560px)}.task-source-status{padding:13px;border-left:3px solid #416e83;background:#0c202e}.task-source-status h3{margin-bottom:7px}.task-source-status p{overflow-wrap:anywhere}.schedule-skips{display:grid;gap:5px;margin:16px 0 0;padding:12px;border-left:3px solid #bc8e4c;background:#2c2417;color:#f4d59b}.schedule-skips span{font-size:13px}@media(max-width:980px){.task-facts{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:620px){.task-tabs{max-block-size:420px}.task-tab{grid-template-columns:1fr;align-items:start;gap:7px;min-height:0}.task-tab-meta{display:flex;justify-items:start;align-items:center;justify-content:space-between;width:100%;min-width:0}.task-detail-panel{padding:14px}.task-detail-head{flex-direction:column}.task-detail-actions{justify-content:flex-start}.task-facts{grid-template-columns:1fr}.derived-events header{flex-direction:column}.derived-event-editor{height:46vh}}
</style>
