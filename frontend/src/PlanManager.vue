<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import HelpTooltip from './HelpTooltip.vue'
import type { Config, Plan, PlanItem, Slot } from './types'

const props = defineProps<{ config: Config; selectedPlanId: string | null; busy: boolean }>()
const emit = defineEmits<{ save: []; 'update:selectedPlanId': [string | null] }>()
const days = ['一', '二', '三', '四', '五', '六', '日']
const hours = Array.from({ length: 24 }, (_, index) => String(index).padStart(2, '0'))
const minutes = Array.from({ length: 60 }, (_, index) => String(index).padStart(2, '0'))
const allDays = () => [0, 1, 2, 3, 4, 5, 6]
const selectedPlan = computed(() => props.config.plans.find((item) => item.id === props.selectedPlanId) || null)
const view = ref<'overview' | 'editor'>('overview')
const draggedSlotId = ref<string | null>(null)
const draggedItemId = ref<string | null>(null)
const draggedPlanId = ref<string | null>(null)
const draggingItemId = ref<string | null>(null)
const editingPlanId = ref<string | null>(null)
const renameDraft = ref('')
const renameInput = ref<HTMLInputElement | null>(null)
const pendingItemRows = ref<Record<string, string[]>>({})
const dayWarnings = ref<Record<string, string>>({})
const openDayPickerId = ref<string | null>(null)

function uid() { return crypto.randomUUID() }
function persist() { emit('save') }
function choosePlan(id: string | null) { emit('update:selectedPlanId', id) }
function pendingRows(plan: Plan) { return pendingItemRows.value[plan.id] || [] }
function newPlan() {
  if (props.busy) return
  const plan: Plan = { id: uid(), name: '新计划', items: [], time_slots: [], startup_mode: 'never', scheduled_enabled: true, completion_action: 'none', completion_delay_minutes: 0, script_ids: [], run_on_startup: false }
  props.config.plans.push(plan)
  choosePlan(plan.id)
  view.value = 'editor'
  persist()
}
function deletePlan(plan: Plan) {
  const id = plan.id
  if (props.busy) return
  const removedAt = props.config.plans.findIndex((item) => item.id === id)
  props.config.plans = props.config.plans.filter((item) => item.id !== id)
  const { [id]: _removed, ...rest } = pendingItemRows.value
  pendingItemRows.value = rest
  choosePlan(props.config.plans[Math.min(removedAt, props.config.plans.length - 1)]?.id || null)
  persist()
}
function openEditor(plan: Plan) {
  if (props.busy) return
  choosePlan(plan.id)
  view.value = 'editor'
}
function returnOverview() { openDayPickerId.value = null; cancelRename(); view.value = 'overview' }
function beginRename(plan: Plan) {
  if (props.busy) return
  editingPlanId.value = plan.id
  renameDraft.value = plan.name
  void nextTick(() => renameInput.value?.focus())
}
function commitRename(plan: Plan) {
  if (editingPlanId.value !== plan.id) return
  const next = renameDraft.value.trim()
  if (next && next !== plan.name) { plan.name = next; persist() }
  editingPlanId.value = null
}
function cancelRename() { editingPlanId.value = null; renameDraft.value = '' }
function scriptFor(item: PlanItem) { return props.config.queues.find((script) => script.id === item.script_id) || null }
function planStatus(plan: Plan) {
  if (!plan.scheduled_enabled) return '定时未启用'
  const slot = plan.time_slots.find((item) => item.enabled)
  return slot ? `定时 ${slot.time}` : '未设置时间'
}
function addSlot(plan: Plan) { plan.time_slots.push({ id: uid(), enabled: true, days: allDays(), time: '09:00' }); persist() }
function removeSlot(plan: Plan, id: string) { plan.time_slots = plan.time_slots.filter((item) => item.id !== id); closeDayPicker(id); persist() }
function addPendingItem(plan: Plan) { pendingItemRows.value = { ...pendingItemRows.value, [plan.id]: [...pendingRows(plan), uid()] } }
function removePendingItem(plan: Plan, rowId: string) { pendingItemRows.value = { ...pendingItemRows.value, [plan.id]: pendingRows(plan).filter((id) => id !== rowId) } }
function choosePendingScript(plan: Plan, rowId: string, event: Event) {
  const scriptId = (event.target as HTMLSelectElement).value
  if (!scriptId || plan.items.some((item) => item.script_id === scriptId)) return
  plan.items.push({ id: uid(), script_id: scriptId, days: allDays() })
  removePendingItem(plan, rowId)
  persist()
}
function removeItem(plan: Plan, id: string) { plan.items = plan.items.filter((item) => item.id !== id); closeDayPicker(id); persist() }
function isDragHandle(event: DragEvent, selector = '.drag-order') { return event.target instanceof Element && !!event.target.closest(selector) }
function beginRowDrag(event: DragEvent, id: string, kind: 'slot' | 'item') {
  if (!isDragHandle(event) || props.busy) { event.preventDefault(); return }
  if (kind === 'slot') draggedSlotId.value = id
  else { draggedItemId.value = id; draggingItemId.value = id }
  event.dataTransfer?.setData('text/plain', id)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
  const row = event.currentTarget as HTMLElement
  event.dataTransfer?.setDragImage(row, 26, Math.min(26, Math.floor(row.offsetHeight / 2)))
}
function beginPlanDrag(event: DragEvent, id: string) {
  if (!isDragHandle(event, '.plan-card-handle') || props.busy) { event.preventDefault(); return }
  draggedPlanId.value = id
  event.dataTransfer?.setData('text/plain', id)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
  event.dataTransfer?.setDragImage(event.currentTarget as HTMLElement, 24, 20)
}
function endItemDrag() { draggedItemId.value = null; draggingItemId.value = null }
function moveBefore<T extends { id: string }>(items: T[], fromId: string | null, targetId: string) {
  const from = items.findIndex((item) => item.id === fromId)
  const target = items.findIndex((item) => item.id === targetId)
  if (from < 0 || target < 0 || from === target) return
  const [moved] = items.splice(from, 1)
  items.splice(target, 0, moved)
  persist()
}
function movePlanBy(planId: string, direction: -1 | 1) {
  if (props.busy) return
  const index = props.config.plans.findIndex((plan) => plan.id === planId)
  const target = index + direction
  if (index < 0 || target < 0 || target >= props.config.plans.length) return
  ;[props.config.plans[index], props.config.plans[target]] = [props.config.plans[target], props.config.plans[index]]
  persist()
}
function setTime(slot: Slot, part: 'hour' | 'minute', value: string) {
  const [hour, minute] = slot.time.split(':')
  slot.time = `${part === 'hour' ? value : hour}:${part === 'minute' ? value : minute}`
  persist()
}
function closeDayPicker(id?: string) { if (!id || openDayPickerId.value === id) openDayPickerId.value = null }
function toggleDayPicker(id: string) { openDayPickerId.value = openDayPickerId.value === id ? null : id }
function setDay(target: Slot | PlanItem, day: number, selected: boolean) {
  if (!selected && target.days.length === 1 && target.days.includes(day)) {
    dayWarnings.value = { ...dayWarnings.value, [target.id]: '请至少选择一个运行日。' }
    openDayPickerId.value = target.id
    return
  }
  target.days = selected ? [...new Set([...target.days, day])].sort((left, right) => left - right) : target.days.filter((item) => item !== day)
  const { [target.id]: _cleared, ...rest } = dayWarnings.value
  dayWarnings.value = rest
  persist()
}
function toggleDay(target: Slot | PlanItem, day: number) { setDay(target, day, !target.days.includes(day)) }
function removeDay(target: Slot | PlanItem, day: number, event: Event) { event.stopPropagation(); setDay(target, day, false) }
function setStartup(plan: Plan) {
  if (plan.startup_mode !== 'never') props.config.plans.forEach((item) => { if (item.id !== plan.id) item.startup_mode = 'never' })
  persist()
}
function issue(plan: Plan) {
  const ids = plan.items.map((item) => item.script_id)
  if (new Set(ids).size !== ids.length) return '不能重复添加同一软件脚本'
  if (ids.some((id) => !props.config.queues.some((script) => script.id === id))) return '包含已删除的软件脚本'
  return ''
}
function closePickerOnOutsidePointer(event: PointerEvent) { if (event.target instanceof Element && !event.target.closest('.weekday-control')) closeDayPicker() }
function closePickerOnEscape(event: KeyboardEvent) { if (event.key === 'Escape') closeDayPicker() }
onMounted(() => { document.addEventListener('pointerdown', closePickerOnOutsidePointer); document.addEventListener('keydown', closePickerOnEscape) })
onBeforeUnmount(() => { document.removeEventListener('pointerdown', closePickerOnOutsidePointer); document.removeEventListener('keydown', closePickerOnEscape) })
</script>

<template>
  <section class="queue-page" aria-label="计划管理">
    <div class="queue-page-head"><div><h2>{{ view === 'overview' ? '计划队列' : '编辑计划' }}</h2><p>{{ view === 'overview' ? '分别管理每个计划的定时、任务和完成后操作。' : '配置修改会自动保存；运行期间锁定。' }}</p></div><button v-if="view === 'overview'" class="primary" :disabled="busy" @click="newPlan">＋ 新建计划</button></div>
    <section v-if="!config.plans.length" class="queue-empty"><div class="queue-empty-mark" aria-hidden="true">△</div><h3>暂无计划</h3><p>还没有创建任何计划。</p></section>
    <template v-else-if="view === 'overview'">
      <section class="plan-overview" aria-label="计划列表"><article v-for="(plan, index) in config.plans" :key="plan.id" class="plan-card" :draggable="!busy" @dragstart="beginPlanDrag($event, plan.id)" @dragend="draggedPlanId = null" @dragover.prevent @drop="moveBefore(config.plans, draggedPlanId, plan.id)"><button class="plan-card-handle" type="button" :disabled="busy" :aria-label="`拖拽调整 ${plan.name} 的显示顺序；可按 Alt 加上或下方向键排序`" @keydown.alt.up.prevent="movePlanBy(plan.id, -1)" @keydown.alt.down.prevent="movePlanBy(plan.id, 1)">⋮⋮</button><div class="plan-card-summary"><b>{{ plan.name }}</b><small>第 {{ index + 1 }} 个计划 · {{ planStatus(plan) }}</small></div><div class="plan-card-actions"><button class="primary" :disabled="busy" :aria-label="`编辑计划：${plan.name}`" @click="openEditor(plan)">编辑计划</button><button class="danger" :disabled="busy" :aria-label="`删除计划：${plan.name}`" @click="deletePlan(plan)">删除计划</button></div></article></section>
    </template>
    <section v-else-if="selectedPlan" class="queue-detail">
      <header><div class="queue-title"><button class="back-button" type="button" :disabled="busy" @click="returnOverview">← 返回计划列表</button><template v-if="editingPlanId === selectedPlan.id"><input ref="renameInput" v-model="renameDraft" aria-label="计划名称" :disabled="busy" @blur="commitRename(selectedPlan)" @keydown.enter.prevent="commitRename(selectedPlan)" @keydown.esc.prevent="cancelRename"></template><template v-else><h3>{{ selectedPlan.name }}</h3><button class="rename-button" :disabled="busy" aria-label="重命名计划" @click="beginRename(selectedPlan)">✎</button></template></div></header>
      <div class="queue-settings-grid">
        <label><span class="field-label">队列类型<HelpTooltip label="队列类型" content="定时队列会在启用的时间槽触发；循环队列仍为后续版本预留，当前不可启用。" /></span><select :value="'scheduled'" :disabled="busy" aria-label="队列类型"><option value="scheduled">定时队列</option><option value="loop" disabled>循环队列（下一版本提供）</option></select></label>
        <label><span class="field-label">启动时运行<HelpTooltip label="启动时运行" content="选择后，应用后端可用时自动运行此计划；每日首次启动时每天只触发一次。" /></span><select v-model="selectedPlan.startup_mode" :disabled="busy" @change="setStartup(selectedPlan)"><option value="never">否</option><option value="always">是</option><option value="daily_first">每日首次启动时</option></select></label>
        <label><span class="field-label">定时运行<HelpTooltip label="定时运行" content="开启后，计划中的已启用定时项才会按设定星期和时间自动触发。" /></span><select v-model="selectedPlan.scheduled_enabled" :disabled="busy" @change="persist"><option :value="true">是</option><option :value="false">否</option></select></label>
        <label><span class="field-label">完成后操作<HelpTooltip label="完成后操作" content="仅整轮任务成功完成后执行；失败、停止或跳过时不会执行该操作。" /></span><select v-model="selectedPlan.completion_action" :disabled="busy" @change="persist"><option value="none">无操作</option><option value="shutdown">关机</option><option value="force_shutdown">强制关机</option><option value="restart">重启</option><option value="hibernate">休眠</option><option value="sleep">睡眠</option><option value="exit_app">退出软件</option><option value="logoff">注销此账户</option></select></label>
        <label><span class="field-label">延时执行（分钟）<HelpTooltip label="延时执行" content="整轮完成后先等待设定分钟数；0 表示不等待，之后仍会进入 60 秒可取消倒计时。" /></span><input v-model.number="selectedPlan.completion_delay_minutes" type="number" min="0" max="10080" :disabled="busy || selectedPlan.completion_action === 'none'" @change="persist"><small>完成后等待，再进入 60 秒可取消倒计时。</small></label>
      </div>
      <p v-if="issue(selectedPlan)" class="queue-diagnostic" role="status">{{ issue(selectedPlan) }}</p>
      <section class="queue-table-section"><div class="queue-table-heading"><h3>定时列表</h3><button :disabled="busy" @click="addSlot(selectedPlan)">＋ 添加定时</button></div><div v-if="!selectedPlan.time_slots.length" class="table-empty">尚未设置定时。</div><div v-else class="queue-table"><div class="queue-table-head"><span>序号</span><span>状态</span><span>执行周期</span><span>执行时间</span><span>操作</span></div><article v-for="(slot, index) in selectedPlan.time_slots" :key="slot.id" class="queue-table-row" :draggable="!busy" @dragstart="beginRowDrag($event, slot.id, 'slot')" @dragend="draggedSlotId = null" @dragover.prevent @drop="moveBefore(selectedPlan.time_slots, draggedSlotId, slot.id)"><b class="drag-order">⋮⋮ {{ index + 1 }}</b><label><input v-model="slot.enabled" type="checkbox" :disabled="busy" @change="persist">启用</label><div class="weekday-control"><div class="weekday-summary" role="button" tabindex="0" :aria-expanded="openDayPickerId === slot.id" @click="toggleDayPicker(slot.id)" @keydown.enter.prevent="toggleDayPicker(slot.id)" @keydown.space.prevent="toggleDayPicker(slot.id)"><span v-for="day in slot.days" :key="day" class="weekday-tag" role="button" tabindex="0" :aria-label="`移除周${days[day]}`" @click="removeDay(slot, day, $event)" @keydown.enter.prevent="setDay(slot, day, false)" @keydown.space.prevent="setDay(slot, day, false)">周{{ days[day] }} <i aria-hidden="true">×</i></span><span class="weekday-arrow" aria-hidden="true">▾</span></div><div v-if="openDayPickerId === slot.id" class="weekday-popover" role="group" aria-label="选择执行周期"><button v-for="(name, day) in days" :key="name" type="button" :class="{ selected: slot.days.includes(day) }" :aria-pressed="slot.days.includes(day)" :disabled="busy" @click="toggleDay(slot, day)"><span class="weekday-check" aria-hidden="true">{{ slot.days.includes(day) ? '✓' : '' }}</span>周{{ name }}</button><p v-if="dayWarnings[slot.id]" class="day-warning" role="status">{{ dayWarnings[slot.id] }}</p></div></div><div class="time-picker"><select :value="slot.time.split(':')[0]" :disabled="busy" aria-label="小时" @change="setTime(slot, 'hour', ($event.target as HTMLSelectElement).value)"><option v-for="hour in hours" :key="hour" :value="hour">{{ hour }} 时</option></select><select :value="slot.time.split(':')[1]" :disabled="busy" aria-label="分钟" @change="setTime(slot, 'minute', ($event.target as HTMLSelectElement).value)"><option v-for="minute in minutes" :key="minute" :value="minute">{{ minute }} 分</option></select></div><button class="textdanger" :disabled="busy" @click="removeSlot(selectedPlan, slot.id)">删除</button></article></div></section>
      <section v-if="selectedPlan.time_slots.length" class="queue-table-section"><div class="queue-table-heading"><h3>任务列表</h3><button class="task-adder" :disabled="busy" @click="addPendingItem(selectedPlan)">＋ 添加任务</button></div><div class="queue-table task-table"><div class="queue-table-head task-head"><span>序号</span><span>软件脚本</span><span>运行周几</span><span>操作</span></div><div v-if="!selectedPlan.items.length && !pendingRows(selectedPlan).length" class="table-empty task-empty"><span class="task-empty-mark" aria-hidden="true">◇</span><strong>尚未选择软件脚本</strong><small>请点击右上角“添加任务”，选择已编辑并保存的软件脚本。</small></div><article v-for="(item, index) in selectedPlan.items" :key="item.id" class="queue-table-row task-row" :class="{ 'is-dragging': draggingItemId === item.id }" :draggable="!busy" @dragstart="beginRowDrag($event, item.id, 'item')" @dragend="endItemDrag" @dragover.prevent @drop="moveBefore(selectedPlan.items, draggedItemId, item.id)"><b class="drag-order">⋮⋮ {{ index + 1 }}</b><span>{{ scriptFor(item)?.name || '已删除的软件脚本' }}</span><div class="weekday-control"><div class="weekday-summary" role="button" tabindex="0" :aria-expanded="openDayPickerId === item.id" @click="toggleDayPicker(item.id)" @keydown.enter.prevent="toggleDayPicker(item.id)" @keydown.space.prevent="toggleDayPicker(item.id)"><span v-for="day in item.days" :key="day" class="weekday-tag" role="button" tabindex="0" :aria-label="`移除周${days[day]}`" @click="removeDay(item, day, $event)" @keydown.enter.prevent="setDay(item, day, false)" @keydown.space.prevent="setDay(item, day, false)">周{{ days[day] }} <i aria-hidden="true">×</i></span><span class="weekday-arrow" aria-hidden="true">▾</span></div><div v-if="openDayPickerId === item.id" class="weekday-popover" role="group" aria-label="选择运行周几"><button v-for="(name, day) in days" :key="name" type="button" :class="{ selected: item.days.includes(day) }" :aria-pressed="item.days.includes(day)" :disabled="busy" @click="toggleDay(item, day)"><span class="weekday-check" aria-hidden="true">{{ item.days.includes(day) ? '✓' : '' }}</span>周{{ name }}</button><p v-if="dayWarnings[item.id]" class="day-warning" role="status">{{ dayWarnings[item.id] }}</p></div></div><span class="row-actions"><button class="textdanger" :disabled="busy" @click="removeItem(selectedPlan, item.id)">删除</button></span></article><article v-for="(rowId, index) in pendingRows(selectedPlan)" :key="rowId" class="queue-table-row task-row task-row-pending"><b class="drag-order muted" aria-hidden="true">⋮⋮ {{ selectedPlan.items.length + index + 1 }}</b><label class="pending-script"><select :disabled="busy" aria-label="选择软件脚本" @change="choosePendingScript(selectedPlan, rowId, $event)"><option value="">未选择脚本</option><option v-for="script in config.queues.filter((candidate) => !selectedPlan!.items.some((item) => item.script_id === candidate.id))" :key="script.id" :value="script.id">{{ script.name }}</option></select><small v-if="!config.queues.length">尚无已保存的软件脚本</small></label><span class="pending-days">选择软件后设置运行周几</span><span class="row-actions"><button class="textdanger" :disabled="busy" @click="removePendingItem(selectedPlan, rowId)">取消</button></span></article></div></section>
    </section>
  </section>
</template>

<style scoped>
.queue-page{max-width:1470px;margin:0 auto;padding-bottom:48px}.queue-page-head,.queue-table-heading,.queue-detail>header{display:flex;align-items:center;justify-content:space-between;gap:16px}.queue-page-head h2{margin:0;font-size:28px}.queue-page-head p{color:#8bb8d5}.queue-empty,.queue-detail,.plan-overview{border:1px solid #284b63;background:#102333;border-radius:10px}.queue-empty{min-height:390px;display:grid;place-content:center;text-align:center;color:#8bb8d5}.queue-empty-mark{font-size:52px;line-height:1;color:#40d7ca;filter:drop-shadow(0 0 14px #40d7ca66)}.queue-empty h3{color:#eff7fb;margin:14px 0 4px}.plan-overview{display:grid;gap:10px;margin-top:20px;padding:12px}.plan-card{display:grid;grid-template-columns:42px minmax(0,1fr) auto;align-items:stretch;min-height:84px;border:1px solid #31546b;border-radius:7px;background:#0c1c29;color:#eef8fb;overflow:hidden}.plan-card:hover{border-color:#45728d;background:#0e2130}.plan-card-handle{border:0;border-right:1px solid #31546b;border-radius:0;background:#102536;color:#92b9ca;cursor:grab}.plan-card-handle:active{cursor:grabbing}.plan-card-handle:focus-visible,.plan-card-actions button:focus-visible{outline:2px solid #54d8d0;outline-offset:-2px}.plan-card-summary{display:grid;align-content:center;gap:5px;min-width:0;padding:15px 18px}.plan-card-summary b{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.plan-card-summary small{color:#8bb8d5}.plan-card-actions{display:flex;align-items:center;gap:10px;padding:14px 16px;border-left:1px solid #31546b;background:#102536}.queue-detail{margin-top:20px}.queue-detail>header{padding:16px 22px;border-bottom:1px solid #29465b}.queue-title{display:flex;align-items:center;gap:8px;min-width:0}.queue-title input{width:min(340px,70vw)}.back-button{margin-right:8px}.rename-button{padding:3px 7px;border:0;color:#55c8ed;background:transparent;font-size:17px;line-height:1}.rename-button:hover:not(:disabled){background:#183246;color:#95e7ff}.queue-settings-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:18px;padding:22px;border-bottom:1px solid #29465b}.queue-settings-grid label{display:grid;gap:7px;color:#b6d8e9}.field-label{display:flex;align-items:center;gap:6px}.queue-settings-grid small{color:#79a3bb}.queue-detail input:not([type='checkbox']),.queue-detail select{width:100%;min-width:0;min-height:37px;padding:8px 10px;border:1px solid #345064;border-radius:5px;background:#0a1722;color:#edf5f9;color-scheme:dark}.queue-detail input:not([type='checkbox']):hover:not(:disabled),.queue-detail select:hover:not(:disabled){border-color:#4f7890;background:#0d1c29}.queue-detail input:not([type='checkbox']):focus,.queue-detail select:focus{outline:2px solid rgba(73,199,207,.28);border-color:#48b9c5}.queue-detail input:not([type='checkbox']):disabled,.queue-detail select:disabled{border-color:#273d4e;background:#0a141d;color:#78919f;cursor:not-allowed}.queue-diagnostic{margin:0;padding:12px 22px;background:#3a2b12;color:#ffd26a}.queue-table-section{padding:22px;border-bottom:1px solid #29465b}.queue-table-section:last-child{border-bottom:0}.queue-table-heading{margin-bottom:14px}.queue-table{border:1px solid #29465b;border-radius:7px;overflow:visible}.queue-table-head,.queue-table-row{display:grid;grid-template-columns:84px 140px minmax(250px,1fr) 220px 110px;align-items:center;gap:12px;padding:12px 14px}.queue-table-head{background:#0d1b27;color:#bbd9e8;font-weight:700}.queue-table-row{border-top:1px solid #29465b;min-height:58px}.task-head,.task-row{grid-template-columns:84px minmax(230px,1fr) minmax(260px,1fr) 110px}.drag-order{cursor:grab;color:#a8c9d7;user-select:none}.drag-order:active{cursor:grabbing}.drag-order.muted{cursor:default;color:#58758a}.task-row.is-dragging{opacity:.42;background:#183649;outline:1px dashed #51d7ca}.weekday-control{position:relative;min-width:0}.weekday-summary{display:flex;align-items:center;min-height:36px;gap:5px;flex-wrap:wrap;padding:5px 28px 5px 7px;border:1px solid #345064;border-radius:5px;background:#0a1722;color:#e5f3f8;cursor:pointer}.weekday-summary:focus-visible{outline:2px solid rgba(73,199,207,.4);border-color:#48b9c5}.weekday-tag{display:inline-flex;align-items:center;gap:4px;padding:2px 5px;border:1px solid #276d78;border-radius:3px;background:#10434a;color:#d8fffb;font-size:12px;white-space:nowrap}.weekday-tag:hover{background:#175965}.weekday-tag:focus-visible{outline:1px solid #70f1e4}.weekday-tag i{font-style:normal;color:#65ded2}.weekday-arrow{position:absolute;right:9px;color:#43d6ca}.weekday-popover{position:absolute;z-index:7;display:grid;grid-template-columns:repeat(2,minmax(95px,1fr));gap:6px;min-width:232px;padding:10px;top:calc(100% + 7px);background:#14253a;border:1px solid #3a6180;border-radius:6px;box-shadow:0 14px 30px #0008}.weekday-popover button{display:flex;align-items:center;gap:7px;min-height:31px;padding:5px 7px;border:1px solid transparent;border-radius:4px;background:transparent;color:#b5ceda;text-align:left}.weekday-popover button:hover:not(:disabled){background:#1b354b}.weekday-popover button.selected{border-color:#276d78;background:#10434a;color:#e1fffb}.weekday-check{width:14px;color:#52dfd1;font-weight:800}.day-warning{grid-column:1/-1;margin:2px 1px 0;color:#ffd26a;font-size:12px}.time-picker{display:flex;gap:7px}.time-picker select{min-width:92px}.task-adder{width:auto!important;min-width:132px!important}.row-actions{display:flex;gap:6px;flex-wrap:wrap}.table-empty{min-height:95px;display:grid;place-items:center;border:1px dashed #31546b;color:#88adbf;text-align:center}.task-empty{min-height:148px;padding:14px;background:#0d1c29;color:#b9d8e7;gap:6px}.task-empty-mark{font-size:34px;line-height:1;color:#4fd7ca;text-shadow:0 0 15px #40d7ca55}.task-empty strong{color:#edf7fb}.task-empty small{max-width:34rem}.task-row-pending{background:#112b3a}.pending-script{display:grid;gap:4px}.pending-script small,.pending-days{color:#86adbf;font-size:12px}.textdanger{color:#ff8795}@media(max-width:980px){.queue-settings-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.plan-card{grid-template-columns:42px minmax(0,1fr)}.plan-card-actions{grid-column:1/-1;justify-content:flex-end;border-top:1px solid #31546b;border-left:0}.queue-table{border:0}.queue-table-head{display:none}.queue-table-row,.task-row{grid-template-columns:1fr;gap:9px;border:1px solid #29465b;border-radius:7px;margin:10px 0}.queue-table-row>b::before{content:'序号 ';color:#86adc4}.weekday-popover{position:static;margin-top:8px;max-width:100%;box-shadow:none}.queue-page-head,.queue-detail>header{align-items:flex-start;flex-direction:column}.queue-title{flex-wrap:wrap}.queue-title input{width:100%}}@media(max-width:620px){.queue-settings-grid{grid-template-columns:1fr}.plan-card-actions{justify-content:stretch;flex-direction:column}.plan-card-actions button{width:100%}.task-adder{width:100%!important}.weekday-popover{grid-template-columns:1fr}}
</style>
