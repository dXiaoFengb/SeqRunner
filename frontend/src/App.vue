<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import TaskStatus from './TaskStatus.vue'
import type { Config, Queue, Runtime, Task } from './types'

type HistoryItem = { task_name: string; state: string; completion_source?: string; error_summary?: string; log_summary: string[] }
type HistoryRun = { id: string; queue_name: string; trigger_source: string; state: string; started_at: string; ended_at?: string; items: HistoryItem[] }
const api = 'http://127.0.0.1:38163'
const config = ref<Config>({ schema_version: 1, queues: [], tasks: [], fired_slots: [] })
const runtime = ref<Runtime>({ busy: false, activeTaskId: null, cooldownSeconds: 0, run: null })
const history = ref<HistoryRun[]>([])
const liveLogs = ref<string[]>([])
const selectedQueueId = ref<string | null>(null)
const message = ref('正在连接本地调度器…')
const selectedQueue = computed(() => config.value.queues.find((item) => item.id === selectedQueueId.value) || null)
const taskBook = computed(() => new Map(config.value.tasks.map((item) => [item.id, item])))
const queueTasks = computed(() => selectedQueue.value?.task_ids.map((id) => taskBook.value.get(id)).filter(Boolean) as Task[] || [])
let socket: WebSocket | undefined
let draggedId: string | null = null

function uid() { return crypto.randomUUID() }
function taskDraft(): Task { return { id: uid(), name: '新任务', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: false, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', timeout_seconds: 600 } }
async function request(path: string, options?: RequestInit) { const response = await fetch(`${api}${path}`, options); if (!response.ok) throw new Error(await response.text()); return response.json() }
async function load() { config.value = await request('/api/config'); selectedQueueId.value ??= config.value.queues[0]?.id || null; runtime.value = await request('/api/runtime'); history.value = (await request('/api/history')).runs; message.value = '本地调度器已连接' }
async function save() { try { config.value = await request('/api/config', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(config.value) }); message.value = '配置已保存'; } catch (error) { message.value = `保存失败：${error}` } }
function addQueue() { const queue: Queue = { id: uid(), name: `队列 ${config.value.queues.length + 1}`, task_ids: [], time_slots: [] }; config.value.queues.push(queue); selectedQueueId.value = queue.id; void save() }
function deleteQueue() { if (!selectedQueue.value) return; const ids = new Set(selectedQueue.value.task_ids); config.value.queues = config.value.queues.filter((queue) => queue.id !== selectedQueue.value?.id); config.value.tasks = config.value.tasks.filter((task) => !ids.has(task.id)); selectedQueueId.value = config.value.queues[0]?.id || null; void save() }
function addTask() { if (!selectedQueue.value) return; const task = taskDraft(); config.value.tasks.push(task); selectedQueue.value.task_ids.push(task.id); void save() }
function removeTask(id: string) { if (!selectedQueue.value) return; selectedQueue.value.task_ids = selectedQueue.value.task_ids.filter((value) => value !== id); config.value.tasks = config.value.tasks.filter((value) => value.id !== id); void save() }
function addSlot() { if (!selectedQueue.value) return; selectedQueue.value.time_slots.push({ id: uid(), enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: '09:00' }); void save() }
function removeSlot(id: string) { if (!selectedQueue.value) return; selectedQueue.value.time_slots = selectedQueue.value.time_slots.filter((slot) => slot.id !== id); void save() }
function moveTask(targetId: string) { if (!selectedQueue.value || !draggedId || draggedId === targetId) return; const ids = selectedQueue.value.task_ids; const from = ids.indexOf(draggedId); const to = ids.indexOf(targetId); ids.splice(from, 1); ids.splice(to, 0, draggedId); draggedId = null; void save() }
function setNullable(task: Task, field: 'working_directory' | 'log_path' | 'success_log' | 'failure_log', event: Event) { const value = (event.target as HTMLInputElement).value.trim(); task[field] = value || null; void save() }
function setArguments(task: Task, event: Event) { const value = (event.target as HTMLInputElement).value.trim(); task.arguments = value ? value.match(/(?:[^\s"]+|"[^"]*")+/g)?.map((part) => part.replaceAll('"', '')) || [] : []; void save() }
async function start() { if (!selectedQueue.value) return; try { await request('/api/start', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ queue_id: selectedQueue.value.id }) }) } catch (error) { message.value = `无法启动：${error}` } }
async function stop() { await request('/api/stop', { method: 'POST' }) }
async function confirm(taskId: string) { await request('/api/confirm', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ task_id: taskId }) }) }
function dayName(day: number) { return ['一', '二', '三', '四', '五', '六', '日'][day] }
function connectEvents() { socket = new WebSocket('ws://127.0.0.1:38163/ws'); socket.onmessage = (event) => { const payload = JSON.parse(event.data); if (payload.type === 'runtime') runtime.value = payload.data; if (payload.type === 'schedule') message.value = payload.data.message; if (payload.type === 'config') config.value = payload.data; if (payload.type === 'log') liveLogs.value = [...liveLogs.value.slice(-39), payload.data.message]; if (payload.type === 'runtime' && !payload.data.busy) void load() }; socket.onclose = () => { message.value = '后端连接已断开'; setTimeout(connectEvents, 1500) } }
onMounted(async () => { try { await load(); connectEvents() } catch (error) { message.value = `无法连接：${error}` } })
onBeforeUnmount(() => socket?.close())
</script>

<template>
  <main class="shell">
    <header class="topbar"><div><p class="kicker">LOCAL CONTROL ROOM</p><h1>顺序任务调度器</h1></div><p class="connection">{{ message }}</p></header>
    <section class="overview"><div class="signal" :class="{ active: runtime.busy }"><span></span>{{ runtime.busy ? '队列运行中' : '调度器空闲' }}</div><div v-if="runtime.run" class="run-name">{{ runtime.run.queue_name }} / {{ runtime.run.state }}<b v-if="runtime.cooldownSeconds"> · 冷却 {{ runtime.cooldownSeconds }} 秒</b></div><div class="actions"><button class="primary" :disabled="runtime.busy || !selectedQueue" @click="start">开始队列</button><button class="danger" :disabled="!runtime.busy" @click="stop">全局停止</button></div></section>
    <div class="workspace">
      <aside class="queue-rail"><div class="rail-head"><h2>队列</h2><button @click="addQueue">新建</button></div><button v-for="queue in config.queues" :key="queue.id" class="queue-item" :class="{ selected: queue.id === selectedQueueId }" @click="selectedQueueId = queue.id"><span>{{ queue.name }}</span><small>{{ queue.task_ids.length }} 项</small></button><p v-if="!config.queues.length" class="empty">先创建一个队列，再添加任务。</p></aside>
      <section v-if="selectedQueue" class="board"><div class="board-head"><input v-model="selectedQueue.name" aria-label="队列名称" @change="save" /><span>{{ queueTasks.length }} 个任务，按此顺序执行</span><button class="text-danger" :disabled="runtime.busy" @click="deleteQueue">删除队列</button></div>
        <div class="task-lane"><article v-for="(task, index) in queueTasks" :key="task.id" class="task-card" draggable="true" @dragstart="draggedId = task.id" @dragover.prevent @drop="moveTask(task.id)"><div class="sequence">{{ String(index + 1).padStart(2, '0') }}</div><div class="task-main"><input v-model="task.name" aria-label="任务名称" @change="save" /><input v-model="task.executable" placeholder="程序完整路径" @change="save" /><div class="detail-grid"><label>参数<input :value="task.arguments.join(' ')" placeholder="可选，空格分隔；路径可用双引号" @change="setArguments(task, $event)" /></label><label>工作目录<input :value="task.working_directory || ''" placeholder="留空则使用程序目录" @change="setNullable(task, 'working_directory', $event)" /></label><label>日志文件<input :value="task.log_path || ''" placeholder="可选，本轮只读取新增行" @change="setNullable(task, 'log_path', $event)" /></label><label>超时（秒）<input v-model.number="task.timeout_seconds" min="1" max="86400" type="number" @change="save" /></label></div><div class="signals"><label><input v-model="task.allow_manual_confirmation" type="checkbox" @change="save" />人工确认</label><label><input v-model="task.allow_process_exit" type="checkbox" @change="save" />正常退出</label><label>成功<input :value="task.success_log || ''" placeholder="关键字或正则" @change="setNullable(task, 'success_log', $event)" /><select v-model="task.success_log_mode" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label><label>失败<input :value="task.failure_log || ''" placeholder="关键字或正则" @change="setNullable(task, 'failure_log', $event)" /><select v-model="task.failure_log_mode" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label></div></div><div class="task-tools"><TaskStatus v-if="runtime.run?.items[index]" :state="runtime.run.items[index].state" :detail="runtime.run.items[index].error_summary" /><button v-if="runtime.activeTaskId === task.id && task.allow_manual_confirmation" @click="confirm(task.id)">确认完成</button><button class="text-danger" :disabled="runtime.busy" @click="removeTask(task.id)">移除</button></div></article><button class="add-line" :disabled="runtime.busy" @click="addTask">＋ 添加任务</button></div>
        <section class="schedule"><div><h2>整队列定时</h2><p>到点后从第一项开始。运行中到点会记录跳过。</p></div><article v-for="slot in selectedQueue.time_slots" :key="slot.id" class="slot"><input v-model="slot.enabled" type="checkbox" :disabled="runtime.busy" @change="save" /><input v-model="slot.time" type="time" :disabled="runtime.busy" @change="save" /><label v-for="day in 7" :key="day"><input v-model="slot.days" :value="day - 1" type="checkbox" :disabled="runtime.busy" @change="save" />{{ dayName(day - 1) }}</label><button class="text-danger" :disabled="runtime.busy" @click="removeSlot(slot.id)">删除</button></article><button :disabled="runtime.busy" @click="addSlot">添加定时</button></section>
        <section class="telemetry"><div><h2>本轮日志摘要</h2><p v-if="!liveLogs.length">启动任务后会显示受管 PID、完成来源与收尾信息。</p><ol v-else><li v-for="(line, index) in liveLogs" :key="index">{{ line }}</li></ol></div><div><h2>历史运行</h2><p v-if="!history.length">尚无已完成的运行记录。</p><article v-for="run in history.slice(0, 5)" :key="run.id" class="history"><strong>{{ run.queue_name }}</strong> · {{ run.trigger_source }} · {{ run.state }}<ul><li v-for="item in run.items" :key="`${run.id}-${item.task_name}`">{{ item.task_name }}：{{ item.completion_source || item.error_summary || item.state }}</li></ul></article></div></section>
      </section>
      <section v-else class="empty-board"><h2>没有活动队列</h2><p>创建一个队列，设置顺序后即可运行。</p></section>
    </div>
  </main>
</template>
