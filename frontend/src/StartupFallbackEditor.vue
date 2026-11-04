<script setup lang="ts">
import { ref, watch } from 'vue'
import type { StartupControl, StartupFallback, Task } from './types'

type WindowCandidate = { handle: number; pid: number; created_at: number; title: string }
const props = defineProps<{ task: Task; busy: boolean; request: (path:string, options?:RequestInit) => Promise<any> }>()
const emit = defineEmits<{ save: [] }>()
const windows = ref<WindowCandidate[]>([])
const selectedWindow = ref<number | null>(null)
const controls = ref<Array<StartupControl & { selectable?: boolean }>>([])
const selectedControl = ref<number | null>(null)
const working = ref(false)
const message = ref('请先自行打开目标软件，再选择窗口和控件；此处不会点击控件。')
function ensure(): StartupFallback { return props.task.startup_fallback ||= { enabled:false, started_signal:'', wait_seconds:30, announcement:null, start:null } }
function change(field:'enabled'|'started_signal'|'wait_seconds', event:Event) { const el=event.target as HTMLInputElement; Object.assign(ensure(), { [field]: field === 'enabled' ? el.checked : field === 'wait_seconds' ? Number(el.value) : el.value }); emit('save') }
function post(path:string, body:unknown) { return props.request(path, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) }) }
watch(() => [props.task.id, props.task.executable], () => { windows.value=[]; controls.value=[]; selectedWindow.value=null; selectedControl.value=null })
async function findWindows() { working.value=true; try { windows.value=await post('/api/ui-log/windows',{executable:props.task.executable}); controls.value=[]; selectedWindow.value=null; message.value=windows.value.length ? '选择目标窗口后读取其按钮。' : '未找到匹配窗口。' } catch { message.value='无法读取目标窗口。' } finally { working.value=false } }
async function findControls() { const win=windows.value.find(item => item.handle === selectedWindow.value); if(!win)return; working.value=true; try { const result=await post('/api/startup-controls',{executable:props.task.executable,pid:win.pid,created_at:win.created_at,window_title:win.title}); controls.value=result.controls || []; selectedControl.value=null; message.value=controls.value.length ? '选择按钮，再指定它的用途。' : '未找到可识别的按钮，请人工处理。' } catch { message.value='无法读取控件；请检查窗口与权限。' } finally { working.value=false } }
function assign(kind:'announcement'|'start') { if(selectedControl.value === null)return; const control=controls.value[selectedControl.value]; if(!control || control.selectable === false)return; ensure()[kind]={window_title:control.window_title, automation_id:control.automation_id, control_name:control.control_name}; emit('save'); message.value=kind === 'start' ? '启动控件已选择，请保存脚本。' : '公告关闭控件已选择，请保存脚本。' }
function clearAnnouncement() { ensure().announcement=null; emit('save') }
</script>

<template>
  <section class="editor-section">
    <div class="section-heading"><div><h3>启动后备</h3></div><small>默认关闭；仅在未观察到启动日志时尝试已选控件。</small></div>
    <label><input type="checkbox" :checked="task.startup_fallback?.enabled || false" :disabled="busy" @change="change('enabled', $event)"> 启用启动后备</label>
    <template v-if="task.startup_fallback?.enabled">
      <p>需先配置日志来源与能够证明任务开始的日志关键词。识别失败时等待人工处理。</p>
      <p v-if="task.preset_id === 'maa'">MAA 可在“版本信息”窗口选择关闭按钮，并在主窗口选择“Link Start!”；实际可识别控件以读取结果为准。</p>
      <div class="grid"><label>任务开始日志关键词<input :value="task.startup_fallback.started_signal" :disabled="busy" @change="change('started_signal', $event)"></label><label>等待时间（秒）<input type="number" min="1" max="3600" :value="task.startup_fallback.wait_seconds" :disabled="busy" @change="change('wait_seconds', $event)"></label></div>
      <p>公告关闭：{{ task.startup_fallback.announcement?.control_name || task.startup_fallback.announcement?.automation_id || '未配置' }}；启动：{{ task.startup_fallback.start?.control_name || task.startup_fallback.start?.automation_id || '未配置' }}</p>
      <div class="diagnostic-actions"><button :disabled="busy || working || !task.executable" @click="findWindows">查找已打开的窗口</button><select v-if="windows.length" v-model="selectedWindow" aria-label="选择启动配置窗口"><option :value="null">选择窗口</option><option v-for="item in windows" :key="item.handle" :value="item.handle">{{ item.title }}</option></select><button :disabled="busy || working || selectedWindow === null" @click="findControls">读取按钮</button><select v-if="controls.length" v-model="selectedControl" aria-label="选择启动配置控件"><option :value="null">选择按钮</option><option v-for="(item,index) in controls" :key="index" :value="index" :disabled="item.selectable === false">{{ item.control_name || item.automation_id }}{{ item.selectable === false ? '（不可用）' : '' }}</option></select><button :disabled="busy || selectedControl === null" @click="assign('announcement')">设为公告关闭</button><button :disabled="busy || selectedControl === null" @click="assign('start')">设为启动</button><button v-if="task.startup_fallback.announcement" :disabled="busy" @click="clearAnnouncement">清除公告控件</button></div>
      <p role="status">{{ message }}</p>
    </template>
  </section>
</template>
