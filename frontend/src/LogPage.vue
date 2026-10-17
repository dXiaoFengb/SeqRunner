<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { CalendarDays, FolderOpen, RotateCcw, Search } from '@lucide/vue'
import type { RunItem } from './types'
import { triggerLabel, stateLabel, sourceLabel, logLabel } from './logLabels'

type HistoryRun = { id: string; queue_name: string; run_mode?: string; trigger_source: string; state: string; started_at: string; items: RunItem[]; scheduler_skips?: string[] }
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
function selectPreset(key: Preset) { const range = rangeFor(key); start.value = range.start; end.value = range.end; selected.value = key; error.value = '' }
function editDates() { selected.value = null; error.value = '' }
function search() { if (!start.value || !end.value) { error.value = '请选择开始和结束日期。'; return }; if (start.value > end.value) { error.value = '开始日期不能晚于结束日期。'; return }; error.value = ''; applied.value = { start: start.value, end: end.value } }
function reset() { selectPreset('week'); search() }
function recordDate(run: HistoryRun) { const date = new Date(run.started_at); return Number.isNaN(date.getTime()) ? '' : dateKey(date) }
const groups = computed(() => {
  const entries = new Map<string, HistoryRun[]>()
  for (const run of props.history) { const day = recordDate(run); if (!day || day < applied.value.start || day > applied.value.end) continue; const group = entries.get(day) || []; group.push(run); entries.set(day, group) }
  return [...entries.entries()].sort(([a], [b]) => b.localeCompare(a)).map(([date, runs]) => ({ date, runs: runs.sort((a, b) => new Date(b.started_at).getTime() - new Date(a.started_at).getTime()) }))
})
watch(() => [props.selectedId, props.history] as const, ([id]) => {
  if (!id) return
  const run = props.history.find((item) => item.id === id)
  if (!run) return
  const day = recordDate(run)
  if (day && (day < applied.value.start || day > applied.value.end)) { start.value = day; end.value = day; applied.value = { start: day, end: day }; selected.value = null; error.value = '' }
}, { immediate: true })
function itemOutcome(item: RunItem) { const reason = item.error_summary || (item.exit_code !== null && item.exit_code !== undefined ? `退出码 ${item.exit_code}` : item.completion_source || '未提供摘要'); return item.state === 'failed' ? `失败：${reason}` : item.state === 'succeeded' ? `成功：${sourceLabel(item.completion_source || '已完成')}` : stateLabel(item.state) }
function runTime(run: HistoryRun) { const value = new Date(run.started_at); return Number.isNaN(value.getTime()) ? '时间未知' : value.toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' }) }
type DetailEntry = { captured_at: string; text: string }
const detail = ref<{ runId: string; id: string; title: string; entries: DetailEntry[]; next: number | null; error: string; loading: boolean } | null>(null)
async function loadDetail(run: HistoryRun, item: RunItem) {
  if (!item.detail_log_id || !props.request) return
  detail.value = { runId: run.id, id: item.detail_log_id, title: item.task_name, entries: [], next: 0, error: '', loading: false }
  await moreDetail()
}
async function moreDetail() {
  const current = detail.value
  if (!current || current.next === null || !props.request || current.loading) return
  current.loading = true
  try {
    const data = await props.request(`/api/history/${encodeURIComponent(current.runId)}/details/${encodeURIComponent(current.id)}?offset=${current.next}`) as { entries: DetailEntry[]; next_offset: number | null }
    current.entries.push(...data.entries); current.next = data.next_offset
  } catch (error) { current.error = `无法读取详细日志：${error instanceof Error ? error.message : String(error)}`; current.next = null }
  finally { current.loading = false }
}
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
</script>

<template>
  <section class="log-page" aria-label="日志">
    <div class="log-filters">
      <div class="filter-label">快捷选择</div>
      <div class="quick-ranges"><button v-for="preset in presets" :key="preset.key" type="button" :class="{ primary: selected === preset.key }" :aria-pressed="selected === preset.key" @click="selectPreset(preset.key)">{{ preset.label }}</button></div>
      <div class="filter-row">
        <div class="group-mode"><span>合并模式</span><strong>按日合并</strong></div>
        <label>开始日期<input v-model="start" type="date" aria-label="开始日期" @input="editDates"></label>
        <label>结束日期<input v-model="end" type="date" aria-label="结束日期" @input="editDates"></label>
        <div class="filter-actions"><button class="primary" type="button" @click="search"><Search aria-hidden="true" />搜索</button><button type="button" @click="reset"><RotateCcw aria-hidden="true" />重置</button></div>
      </div>
      <p v-if="error" class="filter-error" role="alert">{{ error }}</p>
      <div class="cleanup"><label>清理明细截止日期<input v-model="cleanupDate" type="date" aria-label="清理明细截止日期" @input="cleanupCount = null"></label><button type="button" @click="previewCleanup">清理详细日志</button><span v-if="cleanupError" role="status">{{ cleanupError }}</span></div>
      <div v-if="cleanupCount !== null" class="cleanup-confirm" role="alertdialog" aria-label="确认清理详细日志"><p>确定清理 {{ cleanupDate }} 当天及之前的 {{ cleanupCount }} 条详细日志？运行记录与原始软件日志会保留。</p><button class="danger" @click="confirmCleanup">确认清理</button><button @click="cleanupCount = null">取消</button></div>
    </div>
    <div v-if="!groups.length" class="log-empty"><FolderOpen aria-hidden="true" /><strong>{{ history.length ? '没有符合条件的日志' : '暂无运行记录' }}</strong><span>{{ history.length ? '请调整日期范围后重新搜索。' : '运行完成后，记录会显示在这里。' }}</span></div>
    <section v-for="group in groups" :key="group.date" class="log-group"><h2><CalendarDays aria-hidden="true" />{{ group.date }}</h2><article v-for="run in group.runs" :key="run.id" class="run-record" :class="{ selected: selectedId === run.id, failed: runHasFailures(run) }"><div class="run-record-head"><div><b>{{ run.run_mode === 'plan' ? `计划：${run.queue_name}` : run.run_mode === 'all_scripts' ? '全部软件脚本' : run.queue_name }}</b><small>{{ runTime(run) }} · {{ triggerLabel(run.trigger_source) }} · {{ stateLabel(run.state) }}</small></div><button @click="emit('toggle', run.id)">{{ expanded.has(run.id) ? '收起详情' : runHasFailures(run) ? '查看失败详情' : '查看详情' }}</button></div><ul v-if="expanded.has(run.id)"><li v-for="item in run.items" :key="item.task_id" :class="{ 'history-item-failed': item.state === 'failed' }"><b>{{ item.script_name ? item.script_name + ' / ' : '' }}{{ item.task_name }}</b><span>{{ itemOutcome(item) }}</span><small v-if="item.log_summary?.length">日志：{{ item.log_summary.map(logLabel).join('；') }}</small><button v-if="item.detail_log_id" type="button" @click="loadDetail(run, item)">查看详细日志</button><small v-else>{{ item.detail_log_status || '暂无详细日志' }}</small><small v-if="item.detail_log_id && item.detail_log_status">{{ item.detail_log_status }}</small></li></ul><p v-if="run.scheduler_skips?.length" class="schedule-skips"><b>计划跳过</b><span v-for="skip in run.scheduler_skips" :key="skip">{{ skip }}</span></p></article></section>
    <div v-if="detail" class="detail-overlay" @click.self="detail = null"><section class="detail-dialog" role="dialog" aria-modal="true" aria-label="详细日志"><header><h2>{{ detail.title }} · 详细日志</h2><button @click="detail = null">关闭</button></header><p>显示采集时间；日志原文中的时间保持不变。</p><div class="detail-content"><p v-if="detail.error" role="alert">{{ detail.error }}</p><p v-else-if="!detail.entries.length && !detail.loading">暂无详细日志。</p><div v-for="(entry, index) in detail.entries" :key="index"><time>{{ new Date(entry.captured_at).toLocaleString('zh-CN', { hour12: false }) }}</time><pre>{{ entry.text }}</pre></div></div><button v-if="detail.next !== null" :disabled="detail.loading" @click="moreDetail">{{ detail.loading ? '正在读取' : '加载更多' }}</button></section></div>
  </section>
</template>

<style scoped>
.log-page{padding-bottom:48px}.log-filters,.live-log{padding:24px;border:1px solid var(--line);border-radius:8px;background:var(--panel);box-shadow:inset 0 1px rgba(255,255,255,.04)}.filter-label,.filter-row label,.group-mode>span{color:var(--muted);font-size:14px;font-weight:600}.quick-ranges{display:flex;flex-wrap:wrap;gap:9px;margin:12px 0 20px}.filter-row{display:flex;align-items:flex-end;flex-wrap:wrap;gap:16px;border-top:1px solid var(--line);padding-top:20px}.filter-row label,.group-mode{display:grid;gap:7px}.filter-row input{min-width:180px;min-height:38px;padding:8px 10px;border:1px solid var(--line-strong);border-radius:6px;background:var(--panel-deep);color:var(--ink);color-scheme:dark}.filter-row input:focus-visible{border-color:var(--teal);outline:2px solid rgba(22,119,255,.3)}.group-mode strong{display:flex;align-items:center;min-height:38px;padding:0 12px;border:1px solid var(--line-strong);border-radius:6px;color:var(--ink);font-size:14px}.filter-actions{display:flex;gap:9px;margin-left:auto}.filter-actions button{display:inline-flex;align-items:center;gap:7px}.filter-actions svg,.log-group svg{width:16px;height:16px}.filter-error{margin:12px 0 0;color:#f5a2a8}.log-empty{min-height:250px;display:grid;place-content:center;justify-items:center;gap:9px;text-align:center;color:var(--muted)}.log-empty svg{width:42px;height:42px;opacity:.7}.log-empty strong{font-size:18px;color:var(--ink)}.log-group{margin-top:24px}.log-group h2,.live-log h2{display:flex;align-items:center;gap:8px;font-size:18px;margin:0;color:var(--ink)}.live-log{margin-top:24px}.live-log p{color:var(--muted);margin:12px 0 0}.logs{margin-bottom:0}@media(max-width:980px){.filter-actions{margin-left:0}}@media(max-width:620px){.log-filters,.live-log{padding:16px}.filter-row label{width:100%}.filter-row input{width:100%}}
</style>

<style scoped>
.cleanup{display:flex;align-items:end;gap:10px;flex-wrap:wrap;margin-top:20px}.cleanup label{display:grid;gap:7px;color:var(--muted);font-size:14px}.cleanup input{min-height:38px;padding:8px;border:1px solid var(--line-strong);border-radius:6px;color:var(--ink);background:var(--panel-deep);color-scheme:dark}.cleanup-confirm{margin-top:12px;padding:12px;border:1px solid #80602d;border-radius:6px;background:#292316}.cleanup-confirm p{margin:0 0 10px}.cleanup-confirm button+button{margin-left:8px}.detail-overlay{position:fixed;inset:0;z-index:2000;display:grid;place-items:center;padding:24px;background:#000a}.detail-dialog{width:min(100%,1000px);max-height:90vh;display:flex;flex-direction:column;gap:12px;padding:20px;background:var(--panel);border:1px solid var(--line-strong);border-radius:8px}.detail-dialog header{display:flex;justify-content:space-between;align-items:center}.detail-dialog h2{font-size:18px}.detail-dialog p{margin:0;color:var(--muted)}.detail-content{overflow:auto;min-height:140px;padding:12px;background:#0a1722}.detail-content time{color:#91bad2;font-size:12px}.detail-content pre{margin:4px 0 12px;white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;color:var(--ink)}.run-record li button{justify-self:start}
</style>
