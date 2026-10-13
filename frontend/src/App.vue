<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import RunCenter from './RunCenter.vue'
import ScriptCard from './ScriptCard.vue'
import TaskStatus from './TaskStatus.vue'
import type { Config, Preset, Queue, RunItem, Runtime, Task } from './types'
import { useRunnerConnection } from './useRunnerConnection'

type HistoryRun = { id: string; queue_name: string; run_mode?: string; trigger_source: string; state: string; started_at: string; items: RunItem[] }
type DeferredPreset = { presetId: string; path: string }
const config = ref<Config>({ schema_version: 1, queues: [], tasks: [], fired_slots: [] })
const runtime = ref<Runtime>({ busy: false, activeTaskId: null, cooldownSeconds: 0, cooldownUntil: null, runCenter: null, run: null })
const history = ref<HistoryRun[]>([])
const presets = ref<Preset[]>([])
const logs = ref<string[]>([])
const selectedScriptId = ref<string | null>(null)
const expanded = ref(new Set<string>())
const query = ref('')
const page = ref<'scripts' | 'schedule' | 'history' | 'settings'>('scripts')
const notice = ref('正在确认本地后端…')
const backendDetail = ref('')
const draggedTask = ref<string | null>(null)
const draggedScript = ref<string | null>(null)
const selectionIssues = ref<Record<string, string>>({})
const deferredPresets = ref<Record<string, DeferredPreset>>({})
const confirmationPending = ref(false)
const countdownNow = ref(Date.now())
const allRunProblems = ref<string[]>([])
let localRevision = 0
let savedRevision = 0
let saveFlight: Promise<void> | undefined
let countdownTimer: number | undefined

const selectedScript = computed(() => config.value.queues.find((queue) => queue.id === selectedScriptId.value) || null)
const taskBook = computed(() => new Map(config.value.tasks.map((task) => [task.id, task])))
const scriptTasks = (script: Queue) => script.task_ids.map((id) => taskBook.value.get(id)).filter(Boolean) as Task[]
const visibleScripts = computed(() => {
  const text = query.value.trim().toLocaleLowerCase()
  if (!text) return config.value.queues
  return config.value.queues.filter((script) => script.name.toLocaleLowerCase().includes(text) || scriptTasks(script).some((task) => task.name.toLocaleLowerCase().includes(text) || presetLabel(task).toLocaleLowerCase().includes(text)))
})
const hasUnsavedDraft = computed(() => localRevision > savedRevision)
const connectionSummary = computed(() => connection.restOnline.value ? (connection.realtimeState.value === 'online' ? '本地后端与实时状态已连接' : '实时状态正在恢复，保存与运行仍可继续') : '本地后端不可用，需要后端的操作已暂停')
const cooldown = computed(() => {
  const until = runtime.value.cooldownUntil
  if (!until) return runtime.value.cooldownSeconds || 0
  countdownNow.value
  return Math.max(0, Math.ceil((new Date(until).getTime() - Date.now()) / 1000))
})
const diagnosticText = computed(() => JSON.stringify({ API: connection.restOnline.value ? '在线' : '离线', 实时状态: connection.realtimeLabel.value, 关闭码: connection.diagnostic.value.closeCode, 关闭原因: connection.diagnostic.value.closeReason || '无', 重试次数: connection.diagnostic.value.retryCount, 下次重连: connection.diagnostic.value.nextRetryAt || '无', 最近健康时间: connection.diagnostic.value.lastHealthyAt || '尚未建立', 主进程摘要: backendDetail.value || '无' }, null, 2))
const connection = useRunnerConnection(refresh, handleRealtimeEvent)

function uid() { return crypto.randomUUID() }
function directoryOf(path: string) { return path.replace(/[\\/][^\\/]+$/, '') }
function draft(): Task { return { id: uid(), name: '新程序', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 } }
function presetLabel(task: Task) { return presets.value.find((preset) => preset.id === task.preset_id)?.label || '通用程序' }
function presetSummary(script: Queue) { const labels = scriptTasks(script).map(presetLabel).filter((value, index, list) => list.indexOf(value) === index); return labels.join('、') || '尚未添加程序' }
function report(title: string, error: unknown) { notice.value = `${title}：${error instanceof Error ? error.message : String(error)}` }
function clearSelectionIssue(taskId: string) { const next = { ...selectionIssues.value }; delete next[taskId]; selectionIssues.value = next }
function setSelectionIssue(task: Task, message: string) { selectionIssues.value = { ...selectionIssues.value, [task.id]: message } }
function taskProblem(task: Task): string | null { if (!task.executable.trim()) return '未选择程序路径'; if (selectionIssues.value[task.id]) return '预设路径尚未处理'; if (task.preset_id && !task.preset_evidence) return '预设规则尚未应用'; return null }
function scriptProblems(script: Queue) { const tasks = scriptTasks(script); if (!tasks.length) return [`脚本“${script.name}”没有程序`]; return tasks.flatMap((task) => { const problem = taskProblem(task); return problem ? [`脚本“${script.name}”的“${task.name}”${problem}`] : [] }) }
function scriptBlocked(script: Queue) { return scriptProblems(script).length > 0 }

async function refresh() {
  const remoteConfig = await connection.request('/api/config') as Config
  if (!hasUnsavedDraft.value) config.value = remoteConfig
  selectedScriptId.value ??= config.value.queues[0]?.id || null
  runtime.value = await connection.request('/api/runtime') as Runtime
  history.value = (await connection.request('/api/history') as { runs: HistoryRun[] }).runs
  presets.value = await connection.request('/api/presets') as Preset[]
  if (hasUnsavedDraft.value) await flushSave()
  await resumeDeferredPresets()
}
function handleRealtimeEvent(event: { type: string; data: unknown }) {
  if (event.type === 'runtime') { runtime.value = event.data as Runtime; if (confirmationPending.value && runtime.value.runCenter?.currentState !== 'waiting_manual') confirmationPending.value = false }
  if (event.type === 'config' && !hasUnsavedDraft.value) config.value = event.data as Config
  if (event.type === 'schedule') notice.value = (event.data as { message: string }).message
  if (event.type === 'log') logs.value = [...logs.value.slice(-39), (event.data as { message: string }).message]
}
async function flushSave(): Promise<void> {
  if (saveFlight) return saveFlight
  saveFlight = (async () => {
    while (connection.restOnline.value && savedRevision < localRevision) {
      const revision = localRevision
      const snapshot = JSON.parse(JSON.stringify(config.value)) as Config
      try { const accepted = await connection.request('/api/config', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(snapshot) }) as Config; savedRevision = revision; if (revision === localRevision) config.value = accepted } catch (error) { report('保存失败，草稿将保留至后端恢复', error); break }
    }
    if (savedRevision === localRevision && connection.restOnline.value) notice.value = '脚本配置已保存'
  })().finally(() => { saveFlight = undefined })
  return saveFlight
}
function save() { localRevision += 1; if (!connection.restOnline.value) { notice.value = '后端暂不可用，当前编辑已保留为未保存草稿。'; return Promise.resolve() } return flushSave() }
function setExpanded(id: string, value?: boolean) { const next = new Set(expanded.value); (value ?? !next.has(id)) ? next.add(id) : next.delete(id); expanded.value = next }
function newScript() { const script: Queue = { id: uid(), name: `脚本 ${config.value.queues.length + 1}`, task_ids: [], time_slots: [] }; config.value.queues.push(script); selectedScriptId.value = script.id; setExpanded(script.id, true); void save() }
function addTask(script: Queue) { const task = draft(); config.value.tasks.push(task); script.task_ids.push(task.id); setExpanded(script.id, true); void save() }
function removeTask(script: Queue, id: string) { script.task_ids = script.task_ids.filter((taskId) => taskId !== id); config.value.tasks = config.value.tasks.filter((task) => task.id !== id); clearSelectionIssue(id); delete deferredPresets.value[id]; void save() }
function duplicateScript(script: Queue) { const clonedTasks = scriptTasks(script).map((task) => ({ ...structuredClone(task), id: uid(), name: `${task.name} 副本` })); const clone: Queue = { id: uid(), name: `${script.name} 副本`, task_ids: clonedTasks.map((task) => task.id), time_slots: [] }; config.value.tasks.push(...clonedTasks); config.value.queues.push(clone); selectedScriptId.value = clone.id; setExpanded(clone.id, true); notice.value = '已复制脚本；定时槽未复制。'; void save() }
function deleteScript(script: Queue) { if (runtime.value.busy) return; const owned = new Set(script.task_ids); config.value.queues = config.value.queues.filter((item) => item.id !== script.id); config.value.tasks = config.value.tasks.filter((task) => !owned.has(task.id)); if (selectedScriptId.value === script.id) selectedScriptId.value = config.value.queues[0]?.id || null; void save() }
function moveTask(script: Queue, targetId: string) { if (!draggedTask.value || draggedTask.value === targetId) return; const from = script.task_ids.indexOf(draggedTask.value); const to = script.task_ids.indexOf(targetId); if (from < 0 || to < 0) return; script.task_ids.splice(from, 1); script.task_ids.splice(to, 0, draggedTask.value); draggedTask.value = null; void save() }
function moveScript(targetId: string) { if (!draggedScript.value || draggedScript.value === targetId) return; const from = config.value.queues.findIndex((script) => script.id === draggedScript.value); const to = config.value.queues.findIndex((script) => script.id === targetId); if (from < 0 || to < 0) return; const [moved] = config.value.queues.splice(from, 1); config.value.queues.splice(to, 0, moved); draggedScript.value = null; void save() }
function moveScriptBy(script: Queue, direction: -1 | 1) { const from = config.value.queues.findIndex((item) => item.id === script.id); const to = from + direction; if (from < 0 || to < 0 || to >= config.value.queues.length) return; [config.value.queues[from], config.value.queues[to]] = [config.value.queues[to], config.value.queues[from]]; void save() }
function selectPreset(task: Task) { const preset = presets.value.find((item) => item.id === task.preset_id); task.preset_evidence = null; task.allow_manual_confirmation = true; task.allow_process_exit = false; clearSelectionIssue(task.id); delete deferredPresets.value[task.id]; notice.value = preset ? `已选择 ${preset.label}，请选择对应 EXE 后应用规则。` : '通用程序：可自行配置完成信号。'; void save() }
async function applyPreset(task: Task, executable: string) { if (!task.preset_id) return; const applied = await connection.request(`/api/presets/${task.preset_id}/apply`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ executable, task_id: task.id, name: task.name }) }) as Task; Object.assign(task, applied); const next = { ...deferredPresets.value }; delete next[task.id]; deferredPresets.value = next; clearSelectionIssue(task.id); await save() }
async function resumeDeferredPresets() { if (!connection.restOnline.value) return; for (const [taskId, deferred] of Object.entries(deferredPresets.value)) { const task = taskBook.value.get(taskId); if (!task || task.preset_id !== deferred.presetId || task.executable !== deferred.path) continue; try { await applyPreset(task, deferred.path) } catch (error) { report('恢复预设规则失败', error); return } } }
async function choose(task: Task) { const result = await window.desktopRunner?.selectExecutable(task.preset_id); if (!result || result.canceled || !result.path) return; task.executable = result.path; task.working_directory = directoryOf(result.path); await save(); if (task.preset_id && !result.matchesPreset) { const preset = presets.value.find((item) => item.id === task.preset_id); setSelectionIssue(task, `已保留路径，但该 EXE 不符合 ${preset?.label || '当前'} 预设（预期：${preset?.executables.join('、') || '对应程序'}）。请选择正确程序或显式切换通用程序。`); notice.value = '所选 EXE 未套用预设规则，需先处理后才能运行。'; return } if (!task.preset_id) { clearSelectionIssue(task.id); return } if (!connection.restOnline.value) { deferredPresets.value = { ...deferredPresets.value, [task.id]: { presetId: task.preset_id, path: result.path } }; setSelectionIssue(task, '本地后端暂不可用；路径已保留，恢复连接后将自动应用预设规则。'); notice.value = '路径已回填为未保存草稿，等待后端恢复后再应用预设规则。'; return } try { await applyPreset(task, result.path) } catch (error) { setSelectionIssue(task, '预设规则未能应用；路径已保留，请在后端恢复后重试。'); report('无法应用预设规则', error) } }
async function retryPreset(task: Task) { if (!task.executable || !task.preset_id || !connection.restOnline.value) return; try { await applyPreset(task, task.executable) } catch (error) { report('无法应用预设规则', error) } }
function useGenericProgram(task: Task) { task.preset_id = null; task.preset_evidence = null; clearSelectionIssue(task.id); delete deferredPresets.value[task.id]; notice.value = '已切换为通用程序，保留所选路径。'; void save() }
async function runScript(script: Queue) { const problems = scriptProblems(script); if (problems.length) { notice.value = problems.join('；'); return } try { await connection.request('/api/start', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ queue_id: script.id }) }); notice.value = `已开始脚本“${script.name}”` } catch (error) { report('无法运行脚本', error) } }
function extractProblems(error: unknown) { try { const parsed = JSON.parse(error instanceof Error ? error.message : String(error)); return parsed.detail?.problems || [] } catch { return [] } }
async function runAllScripts() { allRunProblems.value = config.value.queues.flatMap(scriptProblems); if (allRunProblems.value.length) { notice.value = '全部脚本尚未完成配置，已列出问题。'; return } try { await connection.request('/api/start-all', { method: 'POST' }); notice.value = '已按脚本卡顺序开始运行全部脚本。' } catch (error) { allRunProblems.value = extractProblems(error); report('无法运行全部脚本', error) } }
async function stop() { try { await connection.request('/api/stop', { method: 'POST' }); notice.value = '已请求停止：只收尾当前受管程序，不会启动下一项。' } catch (error) { report('停止请求未送达', error) } }
async function confirm(taskId: string) { if (confirmationPending.value) return; try { await connection.request('/api/confirm', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ task_id: taskId }) }); confirmationPending.value = true; notice.value = '确认已接收，正在请求程序正常关闭。' } catch (error) { report('人工确认未被接受', error) } }
function setText(task: Task, key: keyof Task, event: Event) { (task as Record<string, unknown>)[key] = (event.target as HTMLInputElement).value.trim() || null; void save() }
function setArgs(task: Task, event: Event) { const text = (event.target as HTMLInputElement).value.trim(); task.arguments = text ? text.match(/(?:[^\s"]+|"[^"]*")+/g)?.map((part) => part.replaceAll('"', '')) || [] : []; void save() }
function addSlot() { if (!selectedScript.value) return; selectedScript.value.time_slots.push({ id: uid(), enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: '09:00' }); void save() }
function removeSlot(id: string) { if (!selectedScript.value) return; selectedScript.value.time_slots = selectedScript.value.time_slots.filter((slot) => slot.id !== id); void save() }
function day(value: number) { return ['一', '二', '三', '四', '五', '六', '日'][value] }
function taskState(taskId: string) { return runtime.value.run?.items.find((item) => item.task_id === taskId) }
async function refreshDiagnostics() { backendDetail.value = (await window.desktopRunner?.getBackendStatus?.())?.detail || '' }
onMounted(async () => { countdownTimer = window.setInterval(() => { countdownNow.value = Date.now() }, 250); await connection.connect(); await refreshDiagnostics(); notice.value = connection.restOnline.value ? '本地后端已就绪；实时状态会自动保持连接。' : '本地后端尚不可用；可继续编辑本地草稿。' })
onBeforeUnmount(() => { if (countdownTimer) window.clearInterval(countdownTimer); connection.dispose() })
</script>

<template>
  <main class="app">
    <aside class="side"><div class="brand"><span>◆</span><b>顺序调度</b><small>本地脚本执行器</small></div><nav><button :class="{ active: page === 'scripts' }" @click="page = 'scripts'">脚本</button><button :class="{ active: page === 'schedule' }" @click="page = 'schedule'">计划</button><button :class="{ active: page === 'history' }" @click="page = 'history'">历史</button><button :class="{ active: page === 'settings' }" @click="page = 'settings'">设置</button></nav><p>只操作本轮受管 PID，不扫描同名程序。</p></aside>
    <section class="content">
      <header><div><h1>{{ page === 'scripts' ? '脚本管理' : page === 'schedule' ? '计划管理' : page === 'history' ? '运行历史' : '连接与设置' }}</h1><p>{{ connectionSummary }}</p></div><div class="actions"><span class="connection-chip" :class="{ offline: !connection.restOnline.value }">API {{ connection.restOnline.value ? '在线' : '离线' }}</span><span class="connection-chip" :class="{ warning: connection.realtimeState.value !== 'online' }">{{ connection.realtimeLabel.value }}</span><button class="danger" :disabled="!runtime.busy || !connection.restOnline.value" @click="stop">全局停止</button></div></header>
      <RunCenter :runtime="runtime" :countdown="cooldown" :confirmation-pending="confirmationPending" @confirm="confirm" />
      <div class="notice" role="status">{{ notice }}<span v-if="hasUnsavedDraft" class="draft-state">有未保存草稿</span></div>
      <section v-if="page === 'scripts'" class="script-page"><div class="toolbar"><input v-model="query" aria-label="搜索脚本" placeholder="搜索脚本、程序或预设"><div><button @click="expanded = new Set(visibleScripts.map((script) => script.id))">全部展开</button><button @click="expanded = new Set()">全部收起</button><button class="primary" :disabled="!connection.restOnline.value || runtime.busy" @click="newScript">＋ 新建脚本</button><button class="run-all" :disabled="!connection.restOnline.value || runtime.busy || !config.queues.length" @click="runAllScripts">▶ 运行全部脚本</button></div></div><div v-if="allRunProblems.length" class="run-problems"><b>无法运行全部脚本</b><ul><li v-for="problem in allRunProblems" :key="problem">{{ problem }}</li></ul></div><p v-if="!visibleScripts.length" class="empty">没有匹配的脚本。新建脚本后，添加需要顺序运行的程序。</p>
        <ScriptCard v-for="script in visibleScripts" :key="script.id" :script="script" :tasks="scriptTasks(script)" :expanded="expanded.has(script.id)" :locked="runtime.busy" :blocked="scriptBlocked(script)" :running="runtime.run?.items.some((item) => item.script_id === script.id) || runtime.run?.queue_name === script.name" :preset-summary="presetSummary(script)" @edit="selectedScriptId = script.id; setExpanded(script.id, true)" @run="runScript(script)" @duplicate="duplicateScript(script)" @remove="deleteScript(script)" @toggle="setExpanded(script.id)" @dragstart="draggedScript = script.id" @drop="moveScript(script.id)" @move="moveScriptBy(script, $event)">
          <div class="editor-head"><input v-model="script.name" aria-label="脚本名称" :disabled="runtime.busy" @change="save"><span>程序按拖拽后的顺序严格执行</span></div>
          <article v-for="(task, index) in scriptTasks(script)" :key="task.id" class="task" :draggable="!runtime.busy" @dragstart="draggedTask = task.id" @dragover.prevent @drop="moveTask(script, task.id)"><button class="grab" title="拖拽程序排序" :disabled="runtime.busy">⠿</button><b class="num">{{ String(index + 1).padStart(2, '0') }}</b><div class="title"><input v-model="task.name" aria-label="程序名称" :disabled="runtime.busy" @change="save"><small>{{ presetLabel(task) }}</small></div><TaskStatus v-if="taskState(task.id)" :state="taskState(task.id)?.state || 'queued'" :detail="taskState(task.id)?.error_summary"/><button class="textdanger" :disabled="runtime.busy" @click="removeTask(script, task.id)">移除</button><div class="form"><label>软件预设<select v-model="task.preset_id" :disabled="runtime.busy" @change="selectPreset(task)"><option :value="null">通用程序</option><option v-for="preset in presets" :key="preset.id" :value="preset.id">{{ preset.label }}</option></select></label><label class="path">程序完整路径<div><input v-model="task.executable" placeholder="请选择 .exe" :disabled="runtime.busy" @change="save"><button :disabled="runtime.busy" @click="choose(task)">选择软件</button></div></label><p v-if="selectionIssues[task.id]" class="selection-warning">{{ selectionIssues[task.id] }}<span><button v-if="task.preset_id" @click="choose(task)">重新选择</button><button v-if="task.preset_id && connection.restOnline.value" @click="retryPreset(task)">应用预设规则</button><button v-if="task.preset_id" @click="useGenericProgram(task)">切换为通用程序并保存</button></span></p><p v-if="task.preset_id" class="evidence">{{ presets.find((preset) => preset.id === task.preset_id)?.note }}<br><template v-if="task.preset_evidence">证据：{{ task.preset_evidence }}</template><template v-else>请先选择匹配 EXE 以应用规则。</template></p><div class="grid"><label>参数<input :value="task.arguments.join(' ')" :disabled="runtime.busy" @change="setArgs(task, $event)"></label><label>工作目录<input :value="task.working_directory || ''" :disabled="runtime.busy" @change="setText(task, 'working_directory', $event)"></label><label>日志文件<input :value="task.log_path || ''" placeholder="仅本轮新增行" :disabled="runtime.busy" @change="setText(task, 'log_path', $event)"></label><label>超时（秒）<input v-model.number="task.timeout_seconds" type="number" min="1" :disabled="runtime.busy" @change="save"></label></div><fieldset><legend>完成信号（首个成功信号锁定）</legend><div class="signals"><label><input v-model="task.allow_manual_confirmation" type="checkbox" :disabled="runtime.busy" @change="save">人工确认</label><label><input v-model="task.allow_process_exit" type="checkbox" :disabled="runtime.busy" @change="save">正常退出</label><label>成功<input :value="task.success_log || ''" placeholder="关键字或正则" :disabled="runtime.busy" @change="setText(task, 'success_log', $event)"><select v-model="task.success_log_mode" :disabled="runtime.busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label><label>失败<input :value="task.failure_log || ''" placeholder="关键字或正则" :disabled="runtime.busy" @change="setText(task, 'failure_log', $event)"><select v-model="task.failure_log_mode" :disabled="runtime.busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label></div></fieldset><button v-if="runtime.activeTaskId === task.id && task.allow_manual_confirmation" class="confirm" :disabled="confirmationPending" @click="confirm(task.id)">{{ confirmationPending ? '确认已接收' : '确认当前程序完成' }}</button></div></article><button class="add" :disabled="runtime.busy || !connection.restOnline.value" @click="addTask(script)">＋ 添加程序</button>
        </ScriptCard></section>
      <section v-else-if="page === 'schedule'" class="page"><h2>整脚本计划</h2><p>计划只触发单条脚本；“运行全部脚本”仅支持手动开始。</p><label class="script-picker">选择脚本<select v-model="selectedScriptId"><option v-for="script in config.queues" :key="script.id" :value="script.id">{{ script.name }}</option></select></label><template v-if="selectedScript"><article v-for="slot in selectedScript.time_slots" :key="slot.id" class="slot"><input v-model="slot.enabled" type="checkbox" :disabled="runtime.busy" @change="save"><input v-model="slot.time" type="time" :disabled="runtime.busy" @change="save"><label v-for="number in 7" :key="number"><input v-model="slot.days" :value="number - 1" type="checkbox" :disabled="runtime.busy" @change="save">周{{ day(number - 1) }}</label><button class="textdanger" :disabled="runtime.busy" @click="removeSlot(slot.id)">删除</button></article><button :disabled="!connection.restOnline.value || runtime.busy" @click="addSlot">添加定时槽</button></template></section>
      <section v-else-if="page === 'history'" class="page"><h2>脚本运行历史</h2><p v-if="!history.length">尚无已完成脚本运行。</p><article v-for="run in history" :key="run.id" class="run-record"><b>{{ run.run_mode === 'all_scripts' ? '全部脚本' : run.queue_name }}</b> · {{ run.trigger_source }} · {{ run.state }}<ul><li v-for="item in run.items" :key="`${item.script_id}-${item.task_id}`"><small v-if="item.script_name">{{ item.script_name }} / </small>{{ item.task_name }}：{{ item.completion_source || item.error_summary || item.state }}</li></ul></article><h2>本轮日志摘要</h2><ol class="logs"><li v-for="(line, index) in logs" :key="index">{{ line }}</li></ol></section>
      <section v-else class="page settings-page"><h2>本地连接</h2><dl><dt>API 状态</dt><dd>{{ connection.restOnline.value ? '在线：保存与运行可用' : '离线：等待后端恢复' }}</dd><dt>实时状态</dt><dd>{{ connection.realtimeLabel.value }}</dd><dt>数据目录</dt><dd>开发模式根目录 data\；便携版 EXE 同级 data\。</dd><dt>隐私边界</dt><dd>只保存本项目脚本、历史和应用日志，不改写目标软件数据。</dd></dl><div class="settings-actions"><button @click="refreshDiagnostics">刷新主进程诊断</button></div><details class="connection-diagnostics"><summary>连接诊断</summary><code>{{ diagnosticText }}</code></details></section>
    </section>
  </main>
</template>
