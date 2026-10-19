<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { Task, UiLogRegion } from './types'

type WindowCandidate = { handle: number; pid: number; created_at: number; title: string; window_class: string; client_width: number; client_height: number }
const props = defineProps<{ task: Task; busy: boolean; request: (path: string, options?: RequestInit) => Promise<any> }>()
const emit = defineEmits<{ save: [] }>()
const windows = ref<WindowCandidate[]>([])
const selected = ref<number | null>(null)
const pending = ref<UiLogRegion | null>(null)
const preview = ref<string | null>(null)
const frame = ref<string | null>(null)
const activeRegion = computed(() => pending.value || props.task.ui_log_region || null)
let timer: ReturnType<typeof setTimeout> | undefined
let generation = 0
let unmounted = false
const working = ref(false)
const message = ref('请自行打开软件，然后选择窗口并框选日志区域。')
watch(() => props.task.id, () => { generation++; windows.value = []; selected.value = null; pending.value = null; preview.value = null; frame.value = null })
watch(() => props.task.executable, () => { generation++; clearTimeout(timer); windows.value = []; selected.value = null; pending.value = null; preview.value = null; frame.value = null; message.value = '程序路径已更改，请重新选择窗口并检查区域。' })
watch([selected, activeRegion, () => props.busy], () => { generation++; frame.value = null; preview.value = null; clearTimeout(timer); void refreshFrame(generation) })
onBeforeUnmount(() => { unmounted = true; generation++; clearTimeout(timer) })
function post(path: string, body: unknown) { return props.request(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) }
function describe(error: unknown) {
  const message = error instanceof Error ? error.message : String(error)
  try { const parsed = JSON.parse(message); if (typeof parsed.detail === 'string') return parsed.detail } catch { /* 普通错误消息 */ }
  return message
}
async function refreshFrame(version: number) {
  const window = windows.value.find(item => item.handle === selected.value)
  const region = activeRegion.value
  if (!window || !region || props.busy || unmounted) return
  if (document.hidden) { timer = setTimeout(() => refreshFrame(version), 1000); return }
  try {
    const result = await post('/api/ui-log/frame', { pid: window.pid, created_at: window.created_at, region })
    if (version === generation && !unmounted) frame.value = `data:image/png;base64,${result.image}`
    try {
      const recognized = await post('/api/ui-log/preview', { pid: window.pid, created_at: window.created_at, region })
      if (version === generation && !unmounted) {
        preview.value = recognized.text
        if (message.value.startsWith('画面预览暂停：') || message.value.startsWith('识别预览暂停：')) message.value = '预览已恢复。'
      }
    } catch (error) {
      if (version === generation && !unmounted) { frame.value = null; preview.value = null; message.value = `识别预览暂停：${describe(error)}` }
    }
  } catch (error) {
    if (version === generation && !unmounted) { frame.value = null; preview.value = null; message.value = `画面预览暂停：${describe(error)}` }
  }
  if (version === generation && !unmounted) timer = setTimeout(() => refreshFrame(version), 1500)
}
async function findWindows() {
  generation++; clearTimeout(timer); working.value = true; pending.value = null; selected.value = null; frame.value = null; preview.value = null
  try {
    windows.value = await post('/api/ui-log/windows', { executable: props.task.executable })
    selected.value = windows.value.length === 1 ? windows.value[0].handle : null
    message.value = windows.value.length ? '选择软件窗口，然后按住鼠标框选日志区域；按 Esc 取消。' : '未找到匹配的窗口，请先打开已配置的软件。'
  } catch (error) { message.value = `查找窗口失败：${describe(error)}` }
  finally { working.value = false }
}
async function selectRegion() {
  const window = windows.value.find(item => item.handle === selected.value)
  if (!window) return
  working.value = true; pending.value = null; preview.value = null
  const full: UiLogRegion = { executable: props.task.executable, window_class: window.window_class, client_width: window.client_width, client_height: window.client_height, x: 0, y: 0, width: window.client_width, height: window.client_height }
  try {
    const result = await post('/api/ui-log/select', { pid: window.pid, created_at: window.created_at, region: full })
    if (result.canceled) { message.value = '已取消框选，原设置保持不变。'; return }
    pending.value = result.region
    const recognized = await post('/api/ui-log/preview', { pid: window.pid, created_at: window.created_at, region: pending.value })
    preview.value = recognized.text
    message.value = '请检查预览内容；画面识别可能有错字或遗漏，确认后保存。'
  } catch (error) { pending.value = null; message.value = `框选或预览失败：${describe(error)}` }
  finally { working.value = false }
}
function saveRegion() {
  if (!pending.value || preview.value === null) return
  props.task.ui_log_region = pending.value
    pending.value = null; message.value = '日志区域已应用，请保存脚本更改。'
  emit('save')
}
</script>

<template>
  <div class="ui-region-editor">
    <h4>界面日志区域 <small>{{ task.preset_id === 'march7th' ? '支持界面文本读取；框选识别可作为备用。' : '可框选界面日志，使用本地画面识别。' }}</small></h4>
    <p>读取软件界面显示的日志；与上面的原始日志文件分开保存。仅支持用户选择的窗口，识别在本机进行。</p>
    <p>{{ task.ui_log_region ? '已设置日志区域；窗口尺寸变化会暂停读取，布局变化时请重新框选。' : '尚未设置日志区域。' }}</p>
    <div class="ui-region-actions">
      <button :disabled="busy || working || !task.executable" @click="findWindows">查找已打开的窗口</button>
      <select v-if="windows.length" v-model="selected" aria-label="软件窗口" :disabled="busy || working">
        <option :value="null">选择软件窗口</option>
        <option v-for="item in windows" :key="item.handle" :value="item.handle">{{ item.title }}（{{ item.pid }}）</option>
      </select>
      <button :disabled="busy || working || selected === null" @click="selectRegion">框选并预览</button>
    </div>
    <p role="status">{{ message }}</p>
    <div v-if="frame && activeRegion" class="visual-preview">
      <div><b>窗口与选区</b><div class="window-frame"><img :src="frame" alt="所选软件窗口实时画面"><span class="selection" :style="{ left: `${activeRegion.x / activeRegion.client_width * 100}%`, top: `${activeRegion.y / activeRegion.client_height * 100}%`, width: `${activeRegion.width / activeRegion.client_width * 100}%`, height: `${activeRegion.height / activeRegion.client_height * 100}%` }"></span></div></div>
      <div><b>选区画面</b><div class="crop-frame" :style="{ aspectRatio: `${activeRegion.client_width} / ${activeRegion.client_height}` }"><div class="crop-image" :style="{ aspectRatio: `${activeRegion.width} / ${activeRegion.height}`, width: `${Math.min(100, activeRegion.width / activeRegion.height * activeRegion.client_height / activeRegion.client_width * 100)}%` }"><img :src="frame" alt="日志区域实时画面" :style="{ width: `${activeRegion.client_width / activeRegion.width * 100}%`, maxWidth: 'none', left: `${-activeRegion.x / activeRegion.width * 100}%`, top: `${-activeRegion.y / activeRegion.height * 100}%` }"></div></div></div>
    </div>
    <div v-if="preview !== null" class="ui-region-preview">
      <b>识别预览</b><pre>{{ preview || '区域内暂无可识别文字。' }}</pre>
      <button v-if="pending" class="primary" :disabled="busy || working" @click="saveRegion">确认并保存区域</button>
    </div>
  </div>
</template>

<style scoped>
.ui-region-editor h4 { margin: 0 0 8px; color: var(--ink); font-size: 16px; }
.ui-region-editor h4 small { color: var(--muted); font-size: 12px; font-weight: normal; }
.ui-region-editor p { margin: 8px 0; color: var(--muted); }
.ui-region-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 12px; }
.ui-region-actions select { min-width: 180px; max-width: 100%; }
.ui-region-preview { margin-top: 12px; padding: 12px; border: 1px solid var(--line); border-radius: 6px; }
.ui-region-preview pre { max-height: 160px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; color: var(--ink); }
.visual-preview { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; margin-top: 12px; }
.window-frame, .crop-frame { position: relative; overflow: hidden; margin-top: 6px; background: #071018; border: 1px solid var(--line); }
.window-frame img { display: block; width: 100%; }
.selection { position: absolute; border: 2px solid #42baff; background: #42baff22; box-sizing: border-box; }
.crop-frame { display: flex; align-items: center; justify-content: center; }
.crop-image { position: relative; overflow: hidden; }
.crop-frame img { position: absolute; }
</style>
