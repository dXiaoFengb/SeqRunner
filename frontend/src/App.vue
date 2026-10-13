<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import RunCenter from './RunCenter.vue'
import ScriptCard from './ScriptCard.vue'
import TaskStatus from './TaskStatus.vue'
import type { Config, Preset, Queue, RunItem, Runtime, Task } from './types'
import { useRunnerConnection } from './useRunnerConnection'

type HistoryRun = { id: string; queue_name: string; run_mode?: string; trigger_source: string; state: string; started_at: string; items: RunItem[] }
type DeferredPreset = { presetId: string; path: string }
type Page = 'scripts' | 'editor' | 'schedule' | 'history' | 'settings'

const config = ref<Config>({ schema_version: 1, queues: [], tasks: [], fired_slots: [] })
const runtime = ref<Runtime>({ busy: false, activeTaskId: null, cooldownSeconds: 0, cooldownUntil: null, runCenter: null, run: null })
const history = ref<HistoryRun[]>([])
const presets = ref<Preset[]>([])
const logs = ref<string[]>([])
const selectedScriptId = ref<string | null>(null)
const editingScriptId = ref<string | null>(null)
const selectedHistoryRunId = ref<string | null>(null)
const expandedHistory = ref(new Set<string>())
const query = ref('')
const page = ref<Page>('scripts')
const notice = ref('正在确认本地后端…')
const backendDetail = ref('')
const appVersion = ref('开发版本读取中')
const draggedTask = ref<string | null>(null)
const draggedScript = ref<string | null>(null)
const selectionIssues = ref<Record<string, string>>({})
const deferredPresets = ref<Record<string, DeferredPreset>>({})
const presetRequestSerial = new Map<string, number>()
const confirmationPending = ref(false)
const countdownNow = ref(Date.now())
const allRunProblems = ref<string[]>([])
let localRevision = 0
let savedRevision = 0
let saveFlight: Promise<void> | undefined
let historyRefreshFlight: Promise<void> | undefined
let terminalHistoryRunId: string | null = null
let countdownTimer: number | undefined

const selectedScript = computed(() => config.value.queues.find((queue) => queue.id === selectedScriptId.value) || null)
const editingScript = computed(() => config.value.queues.find((queue) => queue.id === editingScriptId.value) || null)
const taskBook = computed(() => new Map(config.value.tasks.map((task) => [task.id, task])))
const visibleScripts = computed(() => {
  const text = query.value.trim().toLocaleLowerCase()
  if (!text) return config.value.queues
  return config.value.queues.filter((script) =>
    script.name.toLocaleLowerCase().includes(text) ||
    scriptTasks(script).some((task) => task.name.toLocaleLowerCase().includes(text) || presetLabel(task).toLocaleLowerCase().includes(text)),
  )
})
const hasUnsavedDraft = computed(() => localRevision > savedRevision)
const connectionSummary = computed(() => connection.restOnline.value
  ? (connection.realtimeState.value === 'online' ? '本地后端与实时状态已连接' : '实时状态正在恢复，保存与运行仍可继续')
  : '本地后端不可用，需要后端的操作已暂停')
const cooldown = computed(() => {
  const until = runtime.value.cooldownUntil
  if (!until) return runtime.value.cooldownSeconds || 0
  countdownNow.value
  return Math.max(0, Math.ceil((new Date(until).getTime() - Date.now()) / 1000))
})
const diagnosticText = computed(() => JSON.stringify({
  API: connection.restOnline.value ? '在线' : '离线',
  实时状态: connection.realtimeLabel.value,
  关闭码: connection.diagnostic.value.closeCode,
  关闭原因: connection.diagnostic.value.closeReason || '无',
  重试次数: connection.diagnostic.value.retryCount,
  下次重连: connection.diagnostic.value.nextRetryAt || '无',
  最近健康时间: connection.diagnostic.value.lastHealthyAt || '尚未建立',
  主进程摘要: backendDetail.value || '无',
}, null, 2))
const connection = useRunnerConnection(refresh, handleRealtimeEvent)

function uid() { return crypto.randomUUID() }
function directoryOf(path: string) { return path.replace(/[\\/][^\\/]+$/, '') }
function fileName(path: string) { return path.replace(/^.*[\\/]/, '').toLocaleLowerCase() }
function scriptTasks(script: Queue) { return script.task_ids.map((id) => taskBook.value.get(id)).filter(Boolean) as Task[] }
function draft(): Task {
  return {
    id: uid(), name: '新程序', executable: '', arguments: [], working_directory: null,
    allow_manual_confirmation: true, allow_process_exit: true, log_path: null,
    success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword',
    success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600,
  }
}
function presetFor(task: Task) { return presets.value.find((preset) => preset.id === task.preset_id) }
function presetLabel(task: Task) { return presetFor(task)?.label || '通用程序' }
function presetSummary(script: Queue) {
  const labels = scriptTasks(script).map(presetLabel).filter((value, index, list) => list.indexOf(value) === index)
  return labels.join('、') || '尚未添加程序'
}
function matchesPreset(task: Task, executable = task.executable) {
  const preset = presetFor(task)
  return !task.preset_id || !!preset?.executables.some((name) => name.toLocaleLowerCase() === fileName(executable))
}
function ruleText(task: Task, kind: 'success' | 'failure') {
  const rules = kind === 'success' ? task.success_log_rules : task.failure_log_rules
  return rules.map((rule) => (rule.mode === 'regex' ? '正则：' : '关键字：') + rule.pattern).join('；') || (kind === 'success' ? '无：需人工确认' : '无')
}
function report(title: string, error: unknown) { notice.value = title + '：' + (error instanceof Error ? error.message : String(error)) }
function clearSelectionIssue(taskId: string) {
  const next = { ...selectionIssues.value }
  delete next[taskId]
  selectionIssues.value = next
}
function setSelectionIssue(task: Task, message: string) { selectionIssues.value = { ...selectionIssues.value, [task.id]: message } }
function presetIssue(task: Task): string | null {
  if (!task.preset_id) return null
  if (selectionIssues.value[task.id]) return selectionIssues.value[task.id]
  if (task.executable && !matchesPreset(task)) return '路径与 ' + presetLabel(task) + ' 预设不匹配（预期：' + presetFor(task)?.executables.join('、') + '）。'
  if (task.executable && !task.preset_evidence) return '预设规则尚未应用。'
  return null
}
function taskProblem(task: Task): string | null {
  if (!task.executable.trim()) return '未选择程序路径'
  return presetIssue(task) ? '预设路径尚未处理' : null
}
function scriptProblems(script: Queue) {
  const tasks = scriptTasks(script)
  if (!tasks.length) return ['脚本“' + script.name + '”没有程序']
  return tasks.flatMap((task) => {
    const problem = taskProblem(task)
    return problem ? ['脚本“' + script.name + '”的“' + task.name + '”' + problem] : []
  })
}
function scriptBlocked(script: Queue) { return scriptProblems(script).length > 0 }
function itemReason(item: RunItem) {
  if (item.error_summary) return item.error_summary
  if (item.exit_code !== null && item.exit_code !== undefined) return '退出码 ' + item.exit_code
  return item.completion_source || item.state
}
function itemOutcome(item: RunItem) {
  if (item.state === 'failed') return '失败：' + itemReason(item)
  if (item.state === 'succeeded') return '成功：' + (item.completion_source || '已完成')
  return item.state
}
function runHasFailures(run: HistoryRun) { return run.items.some((item) => item.state === 'failed') }

async function refreshHistory(focusRunId?: string) {
  if (!connection.restOnline.value) return
  if (historyRefreshFlight) return historyRefreshFlight
  historyRefreshFlight = (async () => {
    const payload = await connection.request('/api/history') as { runs: HistoryRun[] }
    history.value = payload.runs
    if (focusRunId) {
      selectedHistoryRunId.value = focusRunId
      expandedHistory.value = new Set([...expandedHistory.value, focusRunId])
    }
  })().catch((error) => report('无法刷新运行历史', error)).finally(() => { historyRefreshFlight = undefined })
  return historyRefreshFlight
}
async function refresh() {
  const remoteConfig = await connection.request('/api/config') as Config
  if (!hasUnsavedDraft.value) config.value = remoteConfig
  selectedScriptId.value ??= config.value.queues[0]?.id || null
  runtime.value = await connection.request('/api/runtime') as Runtime
  presets.value = await connection.request('/api/presets') as Preset[]
  await refreshHistory()
  await reconcileLoadedPresets()
  if (hasUnsavedDraft.value) await flushSave()
  await resumeDeferredPresets()
}
function handleRealtimeEvent(event: { type: string; data: unknown }) {
  if (event.type === 'runtime') {
    runtime.value = event.data as Runtime
    if (confirmationPending.value && runtime.value.runCenter?.currentState !== 'waiting_manual') confirmationPending.value = false
    const finished = runtime.value.run && ['completed', 'stopped'].includes(runtime.value.run.state)
    if (finished && runtime.value.run!.id !== terminalHistoryRunId) {
      terminalHistoryRunId = runtime.value.run!.id
      void refreshHistory(runtime.value.run!.id)
    }
  }
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
      try {
        const accepted = await connection.request('/api/config', {
          method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(snapshot),
        }) as Config
        savedRevision = revision
        if (revision === localRevision) config.value = accepted
      } catch (error) {
        report('保存失败，草稿将保留至后端恢复', error)
        break
      }
    }
    if (savedRevision === localRevision && connection.restOnline.value) notice.value = '脚本配置已保存'
  })().finally(() => { saveFlight = undefined })
  return saveFlight
}
function save() {
  localRevision += 1
  if (!connection.restOnline.value) {
    notice.value = '后端暂不可用，当前编辑已保留为未保存草稿。'
    return Promise.resolve()
  }
  return flushSave()
}
function openEditor(script: Queue) {
  selectedScriptId.value = script.id
  editingScriptId.value = script.id
  page.value = 'editor'
}
function closeEditor() {
  page.value = 'scripts'
  editingScriptId.value = null
}
function openFailureDetails(runId: string) {
  selectedHistoryRunId.value = runId
  expandedHistory.value = new Set([...expandedHistory.value, runId])
  page.value = 'history'
}
function toggleHistory(runId: string) {
  const next = new Set(expandedHistory.value)
  next.has(runId) ? next.delete(runId) : next.add(runId)
  expandedHistory.value = next
}
function newScript() {
  const script: Queue = { id: uid(), name: '脚本 ' + (config.value.queues.length + 1), task_ids: [], time_slots: [] }
  config.value.queues.push(script)
  void save()
  openEditor(script)
}
function addTask(script: Queue) {
  const task = draft()
  config.value.tasks.push(task)
  script.task_ids.push(task.id)
  void save()
}
function removeTask(script: Queue, id: string) {
  script.task_ids = script.task_ids.filter((taskId) => taskId !== id)
  config.value.tasks = config.value.tasks.filter((task) => task.id !== id)
  clearSelectionIssue(id)
  delete deferredPresets.value[id]
  void save()
}
function duplicateScript(script: Queue) {
  const clonedTasks = scriptTasks(script).map((task) => ({ ...structuredClone(task), id: uid(), name: task.name + ' 副本' }))
  const clone: Queue = { id: uid(), name: script.name + ' 副本', task_ids: clonedTasks.map((task) => task.id), time_slots: [] }
  config.value.tasks.push(...clonedTasks)
  config.value.queues.push(clone)
  selectedScriptId.value = clone.id
  notice.value = '已复制脚本；定时槽未复制。'
  void save()
}
function deleteScript(script: Queue) {
  if (runtime.value.busy) return
  const owned = new Set(script.task_ids)
  config.value.queues = config.value.queues.filter((item) => item.id !== script.id)
  config.value.tasks = config.value.tasks.filter((task) => !owned.has(task.id))
  if (selectedScriptId.value === script.id) selectedScriptId.value = config.value.queues[0]?.id || null
  if (editingScriptId.value === script.id) closeEditor()
  void save()
}
function moveTask(script: Queue, targetId: string) {
  if (!draggedTask.value || draggedTask.value === targetId) return
  const from = script.task_ids.indexOf(draggedTask.value)
  const to = script.task_ids.indexOf(targetId)
  if (from < 0 || to < 0) return
  script.task_ids.splice(from, 1)
  script.task_ids.splice(to, 0, draggedTask.value)
  draggedTask.value = null
  void save()
}
function moveScript(targetId: string) {
  if (!draggedScript.value || draggedScript.value === targetId) return
  const from = config.value.queues.findIndex((script) => script.id === draggedScript.value)
  const to = config.value.queues.findIndex((script) => script.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = config.value.queues.splice(from, 1)
  config.value.queues.splice(to, 0, moved)
  draggedScript.value = null
  void save()
}
function moveScriptBy(script: Queue, direction: -1 | 1) {
  const from = config.value.queues.findIndex((item) => item.id === script.id)
  const to = from + direction
  if (from < 0 || to < 0 || to >= config.value.queues.length) return
  ;[config.value.queues[from], config.value.queues[to]] = [config.value.queues[to], config.value.queues[from]]
  void save()
}
function clearAppliedRules(task: Task) {
  task.preset_evidence = null
  task.log_path = null
  task.success_log_rules = []
  task.failure_log_rules = []
  task.allow_manual_confirmation = true
  task.allow_process_exit = false
}
async function applyPresetForCurrentPath(task: Task, executable: string, serial: number) {
  const presetId = task.preset_id
  if (!presetId) return
  const name = task.name === '新程序' ? '' : task.name
  const applied = await connection.request('/api/presets/' + presetId + '/apply', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ executable, task_id: task.id, name }),
  }) as Task
  if (presetRequestSerial.get(task.id) !== serial || task.preset_id !== presetId || task.executable !== executable) return
  Object.assign(task, applied)
  const next = { ...deferredPresets.value }
  delete next[task.id]
  deferredPresets.value = next
  clearSelectionIssue(task.id)
  await save()
}
async function synchronizeExecutable(task: Task, rawPath: string) {
  const executable = rawPath.trim()
  const serial = (presetRequestSerial.get(task.id) || 0) + 1
  presetRequestSerial.set(task.id, serial)
  task.executable = executable
  task.working_directory = executable ? directoryOf(executable) : null
  clearSelectionIssue(task.id)
  if (!task.preset_id || !executable) return save()
  clearAppliedRules(task)
  if (!matchesPreset(task, executable)) {
    setSelectionIssue(task, '已保留路径，但该 EXE 不符合 ' + presetLabel(task) + ' 预设（预期：' + presetFor(task)?.executables.join('、') + '）。请选择正确程序或显式切换通用程序。')
    notice.value = '所选 EXE 未套用预设规则，需先处理后才能运行。'
    return save()
  }
  if (!connection.restOnline.value) {
    deferredPresets.value = { ...deferredPresets.value, [task.id]: { presetId: task.preset_id, path: executable } }
    setSelectionIssue(task, '本地后端暂不可用；路径已保留，恢复连接后将自动应用预设规则。')
    return save()
  }
  try {
    await applyPresetForCurrentPath(task, executable, serial)
  } catch (error) {
    if (presetRequestSerial.get(task.id) === serial) {
      setSelectionIssue(task, '预设规则未能应用；路径已保留，请重试或切换通用程序。')
      await save()
      report('无法应用预设规则', error)
    }
  }
}
function selectPreset(task: Task) {
  clearSelectionIssue(task.id)
  delete deferredPresets.value[task.id]
  if (!task.preset_id) {
    task.preset_evidence = null
    notice.value = '通用程序：可自行配置完成信号。'
    void save()
    return
  }
  clearAppliedRules(task)
  notice.value = task.executable ? '正在为 ' + presetLabel(task) + ' 应用匹配规则。' : '已选择 ' + presetLabel(task) + '，请输入或选择对应 EXE。'
  if (task.executable) void synchronizeExecutable(task, task.executable)
  else void save()
}
async function resumeDeferredPresets() {
  if (!connection.restOnline.value) return
  for (const [taskId, deferred] of Object.entries(deferredPresets.value)) {
    const task = taskBook.value.get(taskId)
    if (!task || task.preset_id !== deferred.presetId || task.executable !== deferred.path) continue
    await synchronizeExecutable(task, deferred.path)
  }
}
async function reconcileLoadedPresets() {
  if (runtime.value.busy || !connection.restOnline.value || hasUnsavedDraft.value) return
  for (const task of config.value.tasks) {
    const preset = presetFor(task)
    if (!preset || !task.executable) continue
    const serial = (presetRequestSerial.get(task.id) || 0) + 1
    presetRequestSerial.set(task.id, serial)
    if (!matchesPreset(task)) {
      clearAppliedRules(task)
      setSelectionIssue(task, '已保留路径，但该 EXE 不符合 ' + preset.label + ' 预设（预期：' + preset.executables.join('、') + '）。请选择正确程序或显式切换通用程序。')
      await save()
      continue
    }
    const rulesMissing = Boolean(preset.requiresLogRules && (!task.success_log_rules.length || !task.failure_log_rules.length))
    if (!task.preset_evidence || rulesMissing) {
      const manualRules = { success_log: task.success_log, success_log_mode: task.success_log_mode, failure_log: task.failure_log, failure_log_mode: task.failure_log_mode }
      await applyPresetForCurrentPath(task, task.executable, serial)
      Object.assign(task, manualRules)
      await save()
    }
  }
}
async function choose(task: Task) {
  const result = await window.desktopRunner?.selectExecutable(task.preset_id)
  if (!result || result.canceled || !result.path) return
  await synchronizeExecutable(task, result.path)
}
function updateExecutable(task: Task, event: Event) { void synchronizeExecutable(task, (event.target as HTMLInputElement).value) }
function retryPreset(task: Task) { if (task.executable && task.preset_id) void synchronizeExecutable(task, task.executable) }
function useGenericProgram(task: Task) {
  task.preset_id = null
  task.preset_evidence = null
  clearSelectionIssue(task.id)
  delete deferredPresets.value[task.id]
  notice.value = '已切换为通用程序，保留所选路径和当前可编辑规则。'
  void save()
}
async function runScript(script: Queue) {
  const problems = scriptProblems(script)
  if (problems.length) {
    notice.value = problems.join('；')
    return
  }
  try {
    await connection.request('/api/start', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ queue_id: script.id }) })
    notice.value = '已开始脚本“' + script.name + '”'
  } catch (error) {
    report('无法运行脚本', error)
  }
}
function extractProblems(error: unknown) {
  try {
    const parsed = JSON.parse(error instanceof Error ? error.message : String(error))
    return parsed.detail?.problems || []
  } catch {
    return []
  }
}
async function runAllScripts() {
  allRunProblems.value = config.value.queues.flatMap(scriptProblems)
  if (allRunProblems.value.length) {
    notice.value = '全部脚本尚未完成配置，已列出问题。'
    return
  }
  try {
    await connection.request('/api/start-all', { method: 'POST' })
    notice.value = '已按脚本卡顺序开始运行全部脚本。'
  } catch (error) {
    allRunProblems.value = extractProblems(error)
    report('无法运行全部脚本', error)
  }
}
async function stop() {
  try {
    await connection.request('/api/stop', { method: 'POST' })
    notice.value = '已请求停止：只收尾当前受管程序，不会启动下一项。'
  } catch (error) {
    report('停止请求未送达', error)
  }
}
async function confirm(taskId: string) {
  if (confirmationPending.value) return
  try {
    await connection.request('/api/confirm', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ task_id: taskId }) })
    confirmationPending.value = true
    notice.value = '确认已接收，正在请求程序正常关闭。'
  } catch (error) {
    report('人工确认未被接受', error)
  }
}
function setText(task: Task, key: keyof Task, event: Event) {
  ;(task as Record<string, unknown>)[key] = (event.target as HTMLInputElement).value.trim() || null
  void save()
}
function setArgs(task: Task, event: Event) {
  const text = (event.target as HTMLInputElement).value.trim()
  task.arguments = text ? text.match(/(?:[^\s"]+|"[^"]*")+/g)?.map((part) => part.replaceAll('"', '')) || [] : []
  void save()
}
function addSlot() {
  if (!selectedScript.value) return
  selectedScript.value.time_slots.push({ id: uid(), enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: '09:00' })
  void save()
}
function removeSlot(id: string) {
  if (!selectedScript.value) return
  selectedScript.value.time_slots = selectedScript.value.time_slots.filter((slot) => slot.id !== id)
  void save()
}
function day(value: number) { return ['一', '二', '三', '四', '五', '六', '日'][value] }
function taskState(taskId: string) { return runtime.value.run?.items.find((item) => item.task_id === taskId) }
async function refreshDiagnostics() { backendDetail.value = (await window.desktopRunner?.getBackendStatus?.())?.detail || '' }

onMounted(async () => {
  countdownTimer = window.setInterval(() => { countdownNow.value = Date.now() }, 250)
  appVersion.value = (await window.desktopRunner?.getBuildInfo?.())?.version || '开发版本不可用'
  document.title = '顺序任务调度器 ' + appVersion.value
  await connection.connect()
  await refreshDiagnostics()
  notice.value = connection.restOnline.value ? '本地后端已就绪；实时状态会自动保持连接。' : '本地后端尚不可用；可继续编辑本地草稿。'
})
onBeforeUnmount(() => {
  if (countdownTimer) window.clearInterval(countdownTimer)
  connection.dispose()
})
</script>

<template>
  <main class="app">
    <aside class="side">
      <div class="brand"><span>◆</span><b>顺序任务调度器 {{ appVersion }}</b><small>本地脚本执行器</small></div>
      <nav>
        <button :class="{ active: page === 'scripts' || page === 'editor' }" @click="page = 'scripts'; editingScriptId = null">脚本</button>
        <button :class="{ active: page === 'schedule' }" @click="page = 'schedule'">计划</button>
        <button :class="{ active: page === 'history' }" @click="page = 'history'">历史</button>
        <button :class="{ active: page === 'settings' }" @click="page = 'settings'">设置</button>
      </nav>
      <p>只操作本轮受管 PID，不扫描同名程序。</p>
    </aside>
    <section class="content">
      <header>
        <div><h1>{{ page === 'scripts' ? '脚本管理' : page === 'editor' ? '编辑脚本' : page === 'schedule' ? '计划管理' : page === 'history' ? '运行历史' : '连接与设置' }}</h1><p>{{ connectionSummary }}</p></div>
        <div class="actions"><span class="connection-chip" :class="{ offline: !connection.restOnline.value }">API {{ connection.restOnline.value ? '在线' : '离线' }}</span><span class="connection-chip" :class="{ warning: connection.realtimeState.value !== 'online' }">{{ connection.realtimeLabel.value }}</span><button class="danger" :disabled="!runtime.busy || !connection.restOnline.value" @click="stop">全局停止</button></div>
      </header>
      <RunCenter :runtime="runtime" :countdown="cooldown" :confirmation-pending="confirmationPending" @confirm="confirm" @show-failures="openFailureDetails" />
      <p class="notice"><span>{{ notice }}</span><span v-if="hasUnsavedDraft" class="draft-state">未保存草稿</span></p>

      <section v-if="page === 'scripts'" class="scripts-page">
        <div class="toolbar"><input v-model="query" placeholder="搜索脚本、程序或预设" aria-label="搜索脚本、程序或预设"><div><button class="primary" :disabled="runtime.busy || !connection.restOnline.value" @click="newScript">＋ 新建脚本</button><button class="run-all" :disabled="!connection.restOnline.value || runtime.busy || !config.queues.length" @click="runAllScripts">▶ 运行全部脚本</button></div></div>
        <div v-if="allRunProblems.length" class="run-problems"><b>无法运行全部脚本</b><ul><li v-for="problem in allRunProblems" :key="problem">{{ problem }}</li></ul></div>
        <p v-if="!visibleScripts.length" class="empty">没有匹配的脚本。新建脚本后，添加需要顺序运行的程序。</p>
        <ScriptCard v-for="script in visibleScripts" :key="script.id" :script="script" :tasks="scriptTasks(script)" :locked="runtime.busy" :blocked="scriptBlocked(script)" :running="runtime.run?.items.some((item) => item.script_id === script.id) || runtime.run?.queue_name === script.name" :preset-summary="presetSummary(script)" @edit="openEditor(script)" @run="runScript(script)" @duplicate="duplicateScript(script)" @remove="deleteScript(script)" @dragstart="draggedScript = script.id" @drop="moveScript(script.id)" @move="moveScriptBy(script, $event)" />
      </section>

      <section v-else-if="page === 'editor' && editingScript" class="editor-page">
        <div class="editor-page-head"><button @click="closeEditor">← 返回脚本管理</button><span>{{ editingScript.task_ids.length }} 个程序 · {{ runtime.busy ? '本轮运行中，配置已锁定' : hasUnsavedDraft ? '存在未保存草稿' : '修改会自动保存' }}</span></div>
        <section class="editor-section editor-basic"><div class="section-heading"><div><span>基本信息</span><h2>脚本名称</h2></div><small>每条脚本按此页程序顺序执行。</small></div><label class="script-name">名称<input v-model="editingScript.name" :disabled="runtime.busy" @change="save"></label></section>
        <section class="editor-section"><div class="section-heading"><div><span>程序顺序</span><h2>程序任务</h2></div><small>拖拽程序卡片可调整执行顺序。</small></div>
          <article v-for="(task, index) in scriptTasks(editingScript)" :key="task.id" class="task editor-task" :draggable="!runtime.busy" @dragstart="draggedTask = task.id" @dragover.prevent @drop="moveTask(editingScript, task.id)">
            <button class="grab" title="拖拽程序排序" :disabled="runtime.busy">⠿</button><b class="num">{{ String(index + 1).padStart(2, '0') }}</b><div class="title"><input v-model="task.name" aria-label="程序名称" :disabled="runtime.busy" @change="save"><small>{{ presetLabel(task) }}</small></div><TaskStatus v-if="taskState(task.id)" :state="taskState(task.id)?.state || 'queued'" :detail="taskState(task.id)?.error_summary"/><button class="textdanger" :disabled="runtime.busy" @click="removeTask(editingScript, task.id)">移除</button>
            <div class="form">
              <section class="form-section"><h3>运行配置</h3><label>软件预设<select v-model="task.preset_id" :disabled="runtime.busy" @change="selectPreset(task)"><option :value="null">通用程序</option><option v-for="preset in presets" :key="preset.id" :value="preset.id">{{ preset.label }}</option></select></label><label class="path">程序完整路径<div><input :value="task.executable" placeholder="请选择 .exe" :disabled="runtime.busy" @change="updateExecutable(task, $event)"><button :disabled="runtime.busy" @click="choose(task)">选择软件</button></div></label><p v-if="presetIssue(task)" class="selection-warning">{{ presetIssue(task) }}<span><button v-if="task.preset_id" @click="choose(task)">重新选择</button><button v-if="task.preset_id && task.executable && connection.restOnline.value" @click="retryPreset(task)">应用预设规则</button><button v-if="task.preset_id" @click="useGenericProgram(task)">切换为通用程序并保存</button></span></p><div class="grid"><label>参数<input :value="task.arguments.join(' ')" :disabled="runtime.busy" @change="setArgs(task, $event)"></label><label>工作目录<input :value="task.working_directory || ''" :disabled="runtime.busy" @change="setText(task, 'working_directory', $event)"></label><label>日志文件<input :value="task.log_path || ''" placeholder="仅本轮新增行" :disabled="runtime.busy" @change="setText(task, 'log_path', $event)"></label><label>超时（秒）<input v-model.number="task.timeout_seconds" type="number" min="1" :disabled="runtime.busy" @change="save"></label></div></section>
              <section v-if="task.preset_id" class="preset-rule-panel form-section"><h3>预设自动规则</h3><b v-if="task.preset_evidence">{{ presetLabel(task) }} 预设自动规则已应用</b><b v-else>等待应用 {{ presetLabel(task) }} 预设规则</b><p>{{ presetFor(task)?.note }}</p><dl v-if="task.preset_evidence"><dt>日志文件</dt><dd>{{ task.log_path || '无：该预设仅人工确认' }}</dd><dt>成功规则</dt><dd>{{ ruleText(task, 'success') }}</dd><dt>失败规则</dt><dd>{{ ruleText(task, 'failure') }}</dd><dt>完成方式</dt><dd>{{ task.allow_manual_confirmation ? '人工确认可用' : '人工确认关闭' }}；{{ task.allow_process_exit ? '正常退出可用' : '正常退出不作为成功' }}</dd></dl><small>证据：{{ task.preset_evidence || presetFor(task)?.evidence }}</small></section>
              <fieldset><legend>完成信号（首个成功信号锁定）</legend><div class="signals"><label><input v-model="task.allow_manual_confirmation" type="checkbox" :disabled="runtime.busy" @change="save">人工确认</label><label><input v-model="task.allow_process_exit" type="checkbox" :disabled="runtime.busy" @change="save">正常退出</label><label>手工附加成功<input :value="task.success_log || ''" placeholder="关键字或正则" :disabled="runtime.busy" @change="setText(task, 'success_log', $event)"><select v-model="task.success_log_mode" :disabled="runtime.busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label><label>手工附加失败<input :value="task.failure_log || ''" placeholder="关键字或正则" :disabled="runtime.busy" @change="setText(task, 'failure_log', $event)"><select v-model="task.failure_log_mode" :disabled="runtime.busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label></div></fieldset>
              <button v-if="runtime.activeTaskId === task.id && task.allow_manual_confirmation" class="confirm" :disabled="confirmationPending" @click="confirm(task.id)">{{ confirmationPending ? '确认已接收' : '确认当前程序完成' }}</button>
            </div>
          </article>
          <button class="add" :disabled="runtime.busy || !connection.restOnline.value" @click="addTask(editingScript)">＋ 添加程序</button>
        </section>
      </section>

      <section v-else-if="page === 'schedule'" class="page"><h2>整脚本计划</h2><p>计划只触发单条脚本；“运行全部脚本”仅支持手动开始。</p><label class="script-picker">选择脚本<select v-model="selectedScriptId"><option v-for="script in config.queues" :key="script.id" :value="script.id">{{ script.name }}</option></select></label><template v-if="selectedScript"><article v-for="slot in selectedScript.time_slots" :key="slot.id" class="slot"><input v-model="slot.enabled" type="checkbox" :disabled="runtime.busy" @change="save"><input v-model="slot.time" type="time" :disabled="runtime.busy" @change="save"><label v-for="number in 7" :key="number"><input v-model="slot.days" :value="number - 1" type="checkbox" :disabled="runtime.busy" @change="save">周{{ day(number - 1) }}</label><button class="textdanger" :disabled="runtime.busy" @click="removeSlot(slot.id)">删除</button></article><button :disabled="!connection.restOnline.value || runtime.busy" @click="addSlot">添加定时槽</button></template></section>
      <section v-else-if="page === 'history'" class="page history-page"><h2>脚本运行历史</h2><p v-if="!history.length">尚无已完成脚本运行。</p><article v-for="run in history" :key="run.id" class="run-record" :class="{ selected: selectedHistoryRunId === run.id, failed: runHasFailures(run) }"><div class="run-record-head"><div><b>{{ run.run_mode === 'all_scripts' ? '全部脚本' : run.queue_name }}</b><small>{{ run.trigger_source }} · {{ run.state }}</small></div><button @click="toggleHistory(run.id)">{{ expandedHistory.has(run.id) ? '收起详情' : runHasFailures(run) ? '查看失败详情' : '查看详情' }}</button></div><ul v-if="expandedHistory.has(run.id)"><li v-for="item in run.items" :key="(item.script_id || '') + '-' + item.task_id" :class="{ 'history-item-failed': item.state === 'failed' }"><b>{{ item.script_name ? item.script_name + ' / ' : '' }}{{ item.task_name }}</b><span>{{ itemOutcome(item) }}</span><small v-if="item.log_summary?.length">日志：{{ item.log_summary.join('；') }}</small></li></ul></article><h2>本轮日志摘要</h2><ol class="logs"><li v-for="(line, index) in logs" :key="index">{{ line }}</li></ol></section>
      <section v-else class="page settings-page"><h2>连接与设置</h2><dl><dt>开发版本</dt><dd>{{ appVersion }}</dd><dt>API 状态</dt><dd>{{ connection.restOnline.value ? '在线：保存与运行可用' : '离线：等待后端恢复' }}</dd><dt>实时状态</dt><dd>{{ connection.realtimeLabel.value }}</dd><dt>数据目录</dt><dd>开发模式根目录 data\；便携版 EXE 同级 data\。</dd><dt>隐私边界</dt><dd>只保存本项目脚本、历史和应用日志，不改写目标软件数据。</dd></dl><div class="settings-actions"><button @click="refreshDiagnostics">刷新主进程诊断</button></div><details class="connection-diagnostics"><summary>连接诊断</summary><code>{{ diagnosticText }}</code></details></section>
    </section>
  </main>
</template>
