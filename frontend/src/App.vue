<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import TaskStatus from './TaskStatus.vue'
import type { Config, Preset, Queue, Runtime, Task } from './types'
import { useRunnerConnection } from './useRunnerConnection'

type HistoryRun = { id: string; queue_name: string; trigger_source: string; state: string; started_at: string; items: Array<{ task_name: string; state: string; completion_source?: string; error_summary?: string; exit_code?: number; log_summary: string[] }> }
const config = ref<Config>({ schema_version: 1, queues: [], tasks: [], fired_slots: [] })
const runtime = ref<Runtime>({ busy: false, activeTaskId: null, cooldownSeconds: 0, run: null })
const history = ref<HistoryRun[]>([])
const presets = ref<Preset[]>([])
const logs = ref<string[]>([])
const selectedScriptId = ref<string | null>(null)
const expanded = ref(new Set<string>())
const query = ref('')
const page = ref<'scripts' | 'schedule' | 'history' | 'settings'>('scripts')
const notice = ref('正在确认本地后端…')
const backendDetail = ref('')
const dragged = ref<string | null>(null)

const selectedScript = computed(() => config.value.queues.find((queue) => queue.id === selectedScriptId.value) || null)
const taskBook = computed(() => new Map(config.value.tasks.map((task) => [task.id, task])))
const scriptTasks = (script: Queue) => script.task_ids.map((id) => taskBook.value.get(id)).filter(Boolean) as Task[]
const visibleScripts = computed(() => {
  const text = query.value.trim().toLocaleLowerCase()
  if (!text) return config.value.queues
  return config.value.queues.filter((script) => script.name.toLocaleLowerCase().includes(text) || scriptTasks(script).some((task) => task.name.toLocaleLowerCase().includes(text) || (presets.value.find((preset) => preset.id === task.preset_id)?.label || '').toLocaleLowerCase().includes(text)))
})
const connection = useRunnerConnection(refresh, handleRealtimeEvent)
const connectionSummary = computed(() => connection.restOnline.value ? (connection.realtimeState.value === 'online' ? '本地后端与实时状态已连接' : '本地后端已连接，实时状态恢复中；操作可继续') : '本地后端不可用，已暂停需要后端的操作')
const diagnosticText = computed(() => JSON.stringify({ ...connection.diagnostic.value, backend: backendDetail.value || undefined }, null, 2))

function uid() { return crypto.randomUUID() }
function draft(): Task { return { id: uid(), name: '新程序', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 } }
async function refresh() {
  config.value = await connection.request('/api/config')
  selectedScriptId.value ??= config.value.queues[0]?.id || null
  runtime.value = await connection.request('/api/runtime')
  history.value = (await connection.request('/api/history')).runs
  presets.value = await connection.request('/api/presets')
}
function handleRealtimeEvent(event: { type: string; data: any }) {
  if (event.type === 'runtime') runtime.value = event.data
  if (event.type === 'config') config.value = event.data
  if (event.type === 'schedule') notice.value = event.data.message
  if (event.type === 'log') logs.value = [...logs.value.slice(-39), event.data.message]
}
function report(title: string, error: unknown) { notice.value = `${title}：${error instanceof Error ? error.message : String(error)}` }
async function save() { try { config.value = await connection.request('/api/config', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(config.value) }); notice.value = '脚本配置已保存' } catch (error) { report('保存失败，原配置保持不变', error) } }
function setExpanded(id: string, value?: boolean) { const next = new Set(expanded.value); (value ?? !next.has(id)) ? next.add(id) : next.delete(id); expanded.value = next }
function newScript() { const script: Queue = { id: uid(), name: `脚本 ${config.value.queues.length + 1}`, task_ids: [], time_slots: [] }; config.value.queues.push(script); selectedScriptId.value = script.id; setExpanded(script.id, true); void save() }
function addTask(script: Queue) { const task = draft(); config.value.tasks.push(task); script.task_ids.push(task.id); setExpanded(script.id, true); void save() }
function removeTask(script: Queue, id: string) { script.task_ids = script.task_ids.filter((taskId) => taskId !== id); config.value.tasks = config.value.tasks.filter((task) => task.id !== id); void save() }
function duplicateScript(script: Queue) {
  const clonedTasks = scriptTasks(script).map((task) => ({ ...structuredClone(task), id: uid(), name: `${task.name} 副本` }))
  const clone: Queue = { id: uid(), name: `${script.name} 副本`, task_ids: clonedTasks.map((task) => task.id), time_slots: [] }
  config.value.tasks.push(...clonedTasks); config.value.queues.push(clone); selectedScriptId.value = clone.id; setExpanded(clone.id, true); notice.value = '已复制脚本；定时槽未复制。'; void save()
}
function deleteScript(script: Queue) {
  if (runtime.value.busy) return
  const owned = new Set(script.task_ids)
  config.value.queues = config.value.queues.filter((item) => item.id !== script.id)
  config.value.tasks = config.value.tasks.filter((task) => !owned.has(task.id))
  if (selectedScriptId.value === script.id) selectedScriptId.value = config.value.queues[0]?.id || null
  void save()
}
function moveTask(script: Queue, targetId: string) { if (!dragged.value || dragged.value === targetId) return; const from = script.task_ids.indexOf(dragged.value); const to = script.task_ids.indexOf(targetId); if (from < 0 || to < 0) return; script.task_ids.splice(from, 1); script.task_ids.splice(to, 0, dragged.value); dragged.value = null; void save() }
function selectPreset(task: Task) { const preset = presets.value.find((item) => item.id === task.preset_id); task.preset_evidence = preset?.evidence || null; task.allow_manual_confirmation = true; task.allow_process_exit = false; notice.value = preset ? `已选择 ${preset.label}，请选择对应 EXE 后应用规则。` : '通用程序：可自行配置完成信号。'; void save() }
async function choose(task: Task) {
  const result = await window.desktopRunner?.selectExecutable(task.preset_id)
  if (!result || result.canceled || !result.path) return
  if (task.preset_id && !result.matchesPreset) { task.preset_id = null; task.preset_evidence = null; task.executable = result.path; task.working_directory = result.path.replace(/[\\/][^\\/]+$/, ''); notice.value = 'EXE 不匹配预设，已切换为通用程序。'; void save(); return }
  if (!task.preset_id) { task.executable = result.path; task.working_directory = result.path.replace(/[\\/][^\\/]+$/, ''); void save(); return }
  try { Object.assign(task, await connection.request(`/api/presets/${task.preset_id}/apply`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ executable: result.path, task_id: task.id, name: task.name }) })); await save() } catch (error) { report('无法应用预设规则', error) }
}
async function runScript(script: Queue) { try { await connection.request('/api/start', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ queue_id: script.id }) }) } catch (error) { report('无法运行脚本', error) } }
async function stop() { try { await connection.request('/api/stop', { method: 'POST' }) } catch (error) { report('停止请求未送达', error) } }
async function confirm(taskId: string) { try { await connection.request('/api/confirm', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ task_id: taskId }) }) } catch (error) { report('人工确认未送达', error) } }
function setText(task: Task, key: keyof Task, event: Event) { (task as Record<string, unknown>)[key] = (event.target as HTMLInputElement).value.trim() || null; void save() }
function setArgs(task: Task, event: Event) { const text = (event.target as HTMLInputElement).value.trim(); task.arguments = text ? text.match(/(?:[^\s"]+|"[^"]*")+/g)?.map((part) => part.replaceAll('"', '')) || [] : []; void save() }
function addSlot() { if (!selectedScript.value) return; selectedScript.value.time_slots.push({ id: uid(), enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: '09:00' }); void save() }
function removeSlot(id: string) { if (!selectedScript.value) return; selectedScript.value.time_slots = selectedScript.value.time_slots.filter((slot) => slot.id !== id); void save() }
function day(value: number) { return ['一', '二', '三', '四', '五', '六', '日'][value] }
function taskState(taskId: string) { return runtime.value.run?.items.find((item) => item.task_id === taskId) }
async function refreshDiagnostics() { backendDetail.value = (await window.desktopRunner?.getBackendStatus?.())?.detail || '' }
onMounted(async () => { await connection.connect(); await refreshDiagnostics(); notice.value = connection.restOnline.value ? '本地后端已就绪；实时状态会自动保持连接。' : '本地后端尚不可用；请展开连接诊断查看下一次重试。' })
onBeforeUnmount(() => connection.dispose())
</script>

<template>
  <main class="app">
    <aside class="side"><div class="brand"><span>◆</span><b>顺序调度</b><small>本地脚本执行器</small></div><nav><button :class="{ active: page === 'scripts' }" @click="page = 'scripts'">脚本</button><button :class="{ active: page === 'schedule' }" @click="page = 'schedule'">计划</button><button :class="{ active: page === 'history' }" @click="page = 'history'">历史</button><button :class="{ active: page === 'settings' }" @click="page = 'settings'">设置</button></nav><p>只操作本轮受管 PID，不扫描同名程序。</p></aside>
    <section class="content">
      <header><div><h1>{{ page === 'scripts' ? '脚本管理' : page === 'schedule' ? '计划管理' : page === 'history' ? '运行历史' : '连接与设置' }}</h1><p>{{ connectionSummary }}</p></div><div class="actions"><span :class="{ ok: connection.restOnline.value, warning: connection.realtimeState.value !== 'online' }">API {{ connection.restOnline.value ? '在线' : '离线' }} · {{ connection.realtimeLabel.value }}</span><button class="danger" :disabled="!runtime.busy || !connection.restOnline.value" @click="stop">全局停止</button></div></header>
      <div class="notice">{{ notice }}<details><summary>连接诊断</summary><code>{{ diagnosticText }}</code></details></div>
      <section v-if="page === 'scripts'" class="script-page"><div class="toolbar"><input v-model="query" aria-label="搜索脚本" placeholder="搜索脚本、程序或预设"><div><button @click="expanded = new Set(visibleScripts.map((script) => script.id))">全部展开</button><button @click="expanded = new Set()">全部收起</button><button class="primary" :disabled="!connection.restOnline.value || runtime.busy" @click="newScript">＋ 新建脚本</button></div></div><p v-if="!visibleScripts.length" class="empty">没有匹配的脚本。新建脚本后，添加需要顺序运行的程序。</p>
        <article v-for="script in visibleScripts" :key="script.id" class="script-card" :class="{ expanded: expanded.has(script.id) }"><div class="script-summary"><span class="grab">⠿</span><div class="script-title"><b>{{ script.name }}</b><small>{{ scriptTasks(script).length }} 个程序 · {{ scriptTasks(script).map((task) => presets.find((preset) => preset.id === task.preset_id)?.label || '通用程序').filter((value, index, list) => list.indexOf(value) === index).join('、') || '尚未添加程序' }}</small></div><TaskStatus v-if="runtime.run?.queue_name === script.name" :state="runtime.run.state" /><div class="card-actions"><button :disabled="runtime.busy" @click="selectedScriptId = script.id; setExpanded(script.id, true)">编辑脚本</button><button class="run" :disabled="runtime.busy || !connection.restOnline.value || !script.task_ids.length" @click="runScript(script)">运行</button><details class="more"><summary>更多</summary><button :disabled="runtime.busy" @click="duplicateScript(script)">复制脚本</button><button class="textdanger" :disabled="runtime.busy" @click="deleteScript(script)">删除脚本</button></details><button class="chevron" @click="setExpanded(script.id)">{{ expanded.has(script.id) ? '⌃' : '⌄' }}</button></div></div>
          <div v-if="expanded.has(script.id)" class="script-editor"><div class="editor-head"><input v-model="script.name" aria-label="脚本名称" @change="save"><span>程序按拖拽后的顺序严格执行</span></div><article v-for="(task, index) in scriptTasks(script)" :key="task.id" class="task" draggable="true" @dragstart="dragged = task.id" @dragover.prevent @drop="moveTask(script, task.id)"><button class="grab" title="拖拽排序">⠿</button><b class="num">{{ String(index + 1).padStart(2, '0') }}</b><div class="title"><input v-model="task.name" aria-label="程序名称" @change="save"><small>{{ presets.find((preset) => preset.id === task.preset_id)?.label || '通用程序' }}</small></div><TaskStatus v-if="taskState(task.id)" :state="taskState(task.id)?.state || 'queued'" :detail="taskState(task.id)?.error_summary"/><button class="textdanger" :disabled="runtime.busy" @click="removeTask(script, task.id)">移除</button><div class="form"><label>软件预设<select v-model="task.preset_id" :disabled="runtime.busy" @change="selectPreset(task)"><option :value="null">通用程序</option><option v-for="preset in presets" :key="preset.id" :value="preset.id">{{ preset.label }}</option></select></label><label class="path">程序完整路径<div><input v-model="task.executable" placeholder="请选择 .exe" @change="save"><button :disabled="runtime.busy || !connection.restOnline.value" @click="choose(task)">选择软件</button></div></label><p v-if="task.preset_evidence" class="evidence">{{ presets.find((preset) => preset.id === task.preset_id)?.note }}<br>证据：{{ task.preset_evidence }}</p><div class="grid"><label>参数<input :value="task.arguments.join(' ')" @change="setArgs(task, $event)"></label><label>工作目录<input :value="task.working_directory || ''" @change="setText(task, 'working_directory', $event)"></label><label>日志文件<input :value="task.log_path || ''" placeholder="仅本轮新增行" @change="setText(task, 'log_path', $event)"></label><label>超时（秒）<input v-model.number="task.timeout_seconds" type="number" min="1" @change="save"></label></div><fieldset><legend>完成信号（首个成功信号锁定）</legend><div class="signals"><label><input v-model="task.allow_manual_confirmation" type="checkbox" @change="save">人工确认</label><label><input v-model="task.allow_process_exit" type="checkbox" @change="save">正常退出</label><label>成功<input :value="task.success_log || ''" placeholder="关键字或正则" @change="setText(task, 'success_log', $event)"><select v-model="task.success_log_mode" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label><label>失败<input :value="task.failure_log || ''" placeholder="关键字或正则" @change="setText(task, 'failure_log', $event)"><select v-model="task.failure_log_mode" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label></div></fieldset><button v-if="runtime.activeTaskId === task.id && task.allow_manual_confirmation" class="confirm" @click="confirm(task.id)">确认当前程序完成</button></div></article><button class="add" :disabled="runtime.busy || !connection.restOnline.value" @click="addTask(script)">＋ 添加程序</button></div>
        </article></section>
      <section v-else-if="page === 'schedule'" class="page"><h2>整脚本计划</h2><p>到点从脚本的第一个程序开始；运行中到点仅记录跳过。</p><label class="script-picker">选择脚本<select v-model="selectedScriptId"><option v-for="script in config.queues" :key="script.id" :value="script.id">{{ script.name }}</option></select></label><template v-if="selectedScript"><article v-for="slot in selectedScript.time_slots" :key="slot.id" class="slot"><input v-model="slot.enabled" type="checkbox" @change="save"><input v-model="slot.time" type="time" @change="save"><label v-for="number in 7" :key="number"><input v-model="slot.days" :value="number - 1" type="checkbox" @change="save">周{{ day(number - 1) }}</label><button class="textdanger" @click="removeSlot(slot.id)">删除</button></article><button :disabled="!connection.restOnline.value || runtime.busy" @click="addSlot">添加定时槽</button></template></section>
      <section v-else-if="page === 'history'" class="page"><h2>脚本运行历史</h2><p v-if="!history.length">尚无已完成脚本运行。</p><article v-for="run in history" :key="run.id" class="run"><b>{{ run.queue_name }}</b> · {{ run.trigger_source }} · {{ run.state }}<ul><li v-for="item in run.items" :key="item.task_name">{{ item.task_name }}：{{ item.completion_source || item.error_summary || item.state }}</li></ul></article><h2>本轮日志摘要</h2><ol class="logs"><li v-for="(line, index) in logs" :key="index">{{ line }}</li></ol></section>
      <section v-else class="page"><h2>本地连接</h2><dl><dt>API 状态</dt><dd>{{ connection.restOnline.value ? '在线：保存与运行可用' : '离线：等待后端恢复' }}</dd><dt>实时状态</dt><dd>{{ connection.realtimeLabel.value }}</dd><dt>数据目录</dt><dd>开发模式根目录 data\；便携版 EXE 同级 data\。</dd><dt>隐私边界</dt><dd>只保存本项目脚本、历史和应用日志，不改写目标软件数据。</dd></dl><button @click="refreshDiagnostics">刷新主进程诊断</button></section>
    </section>
  </main>
</template>
