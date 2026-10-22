<script setup lang="ts">
import UiLogRegionEditor from './UiLogRegionEditor.vue'
import type { MaaEndAdapter, MAAAdapter, Preset, Queue, Task } from './types'

const props = defineProps<{
  script: Queue
  task: Task
  presets: Preset[]
  busy: boolean
  saveLabel: string
  saveState: 'saved' | 'saving' | 'draft' | 'failed'
  selectionIssue?: string
  request: (path: string, options?: RequestInit) => Promise<unknown>
  directoryOf: (path: string) => string
  presetLabel: (task: Task | null) => string
  presetFor: (task: Task) => Preset | undefined
  presetRuleText: (task: Task, type: 'success' | 'failure') => string
  changePreset: (task: Task, event: Event) => void
  choose: (task: Task) => Promise<void>
  chooseInstallDirectory: (task: Task, executable: 'MAA.exe' | 'MaaEnd.exe') => Promise<void>
  updateInstallDirectory: (task: Task, executable: 'MAA.exe' | 'MaaEnd.exe', event: Event) => void
  updateExecutable: (task: Task, event: Event) => void
  chooseLogDirectory: (task: Task) => Promise<void>
  setText: (task: Task, key: 'working_directory' | 'log_path' | 'success_log' | 'failure_log', event: Event) => void
  setLogPatterns: (task: Task, event: Event) => void
  setArgs: (task: Task, event: Event, field?: 'arguments' | 'game_arguments') => void
  useGenericProgram: (task: Task) => void
  chooseStandalone: (target: 'adb' | 'game', task: Task) => Promise<void>
  ensureMaa: (task: Task) => MAAAdapter
  ensureMaaEnd: (task: Task) => MaaEndAdapter
  checkAdb: (task: Task) => Promise<void>
  save: () => Promise<unknown>
  saveEditorChanges: () => Promise<boolean>
  stop: () => Promise<void>
  close: () => void
  remove: (script: Queue) => void
}>()

const isMaa = () => props.task.preset_id === 'maa'
const isMaaEnd = () => props.task.preset_id === 'maaend'
function rename(event: Event) { const value = (event.target as HTMLInputElement).value; props.task.name = value; props.script.name = value; void props.save() }
</script>

<template>
  <section class="editor-page">
    <div class="editor-navigation"><span class="editor-breadcrumb">脚本管理 / 编辑软件</span><div class="editor-navigation-actions"><button v-if="busy" class="danger" @click="stop">全局停止</button><button @click="close">← 返回</button></div></div>
    <section class="editor-sheet specialized-editor">
      <div class="editor-sheet-title"><div><h2>{{ task.name }} 脚本配置</h2><small>按顺序完成预设、程序、运行和日志配置；所有更改仅在点击保存后写入。</small></div><div class="editor-sheet-actions"><b>{{ presetLabel(task) }}</b><small :class="'save-' + saveState">{{ saveLabel }}</small><button class="primary" :disabled="busy || saveState === 'saving'" @click="saveEditorChanges">{{ saveState === 'saving' ? '保存中…' : '保存更改' }}</button></div></div>
      <aside class="editor-guidance"><b>配置顺序</b><span>先选择软件预设，再确认脚本名称和程序路径；其余设置保持默认即可按需调整。</span></aside>

      <section class="editor-section editor-preset-step"><div class="section-heading"><div><h3>1. 软件预设</h3></div><small>选择后只在当前草稿应用模板规则，不会自动保存。</small></div><label>软件预设<select :value="task.preset_id || ''" :disabled="busy" @change="changePreset(task, $event)"><option value="">通用程序</option><option v-for="preset in presets" :key="preset.id" :value="preset.id">{{ preset.label }}</option></select></label></section>

      <section class="editor-section"><div class="section-heading"><div><h3>2. 脚本与程序</h3></div><small>名称会出现在脚本、计划、运行中心和历史记录中。</small></div><div class="basic-grid"><label class="script-name">软件脚本名称<input :value="task.name" aria-label="软件名称" :disabled="busy" @change="rename"></label><label v-if="isMaa()" class="path">MAA 安装目录<div><input :value="task.maa_adapter?.installation_directory || directoryOf(task.executable)" placeholder="请选择含 MAA.exe 的目录" :disabled="busy" @change="updateInstallDirectory(task, 'MAA.exe', $event)"><button :disabled="busy" @click="chooseInstallDirectory(task, 'MAA.exe')">选择目录</button></div></label><label v-else-if="isMaaEnd()" class="path">MaaEnd 安装目录<div><input :value="task.maaend_adapter?.installation_directory || directoryOf(task.executable)" placeholder="请选择含 MaaEnd.exe 的目录" :disabled="busy" @change="updateInstallDirectory(task, 'MaaEnd.exe', $event)"><button :disabled="busy" @click="chooseInstallDirectory(task, 'MaaEnd.exe')">选择目录</button></div></label><label v-else class="path">程序完整路径<div><input :value="task.executable" placeholder="请选择 .exe" :disabled="busy" @change="updateExecutable(task, $event)"><button :disabled="busy" @click="choose(task)">选择软件</button></div></label></div><p v-if="selectionIssue" class="selection-warning">{{ selectionIssue }}<span><button @click="choose(task)">重新选择</button><button @click="useGenericProgram(task)">切换通用程序</button></span></p></section>

      <section class="editor-section editor-run-config"><div class="section-heading"><div><h3>3. 常用运行设置</h3></div><small>保留默认值即可；需要时再设置参数、工作目录或超时。</small></div><div class="grid"><label>超时（秒）<input v-model.number="task.timeout_seconds" type="number" min="1" :disabled="busy" @change="save"></label><label>启动参数<input :value="task.arguments.join(' ')" :disabled="busy" @change="setArgs(task, $event)"></label><label>工作目录<input :value="task.working_directory || ''" :disabled="busy" @change="setText(task, 'working_directory', $event)"></label></div></section>

      <section class="editor-section"><div class="section-heading"><div><h3>4. 日志来源</h3></div><small>可读取单文件或直接子文件夹；脚本内部日志不会复制到本软件目录。</small></div><div class="grid"><label class="path">日志文件或文件夹<div><input :value="task.log_path || ''" :disabled="busy" @change="setText(task, 'log_path', $event)"><button :disabled="busy" @click="chooseLogDirectory(task)">选择文件夹</button></div></label><label>日志文件匹配<input :value="task.log_patterns || '*.log;*.txt'" placeholder="*.log;*.txt" :disabled="busy" @change="setLogPatterns(task, $event)"></label></div><UiLogRegionEditor :task="task" :busy="busy" :request="request" @save="save" /></section>

      <section class="editor-section"><div class="section-heading"><div><h3>5. 完成信号</h3></div><small>{{ task.allow_manual_confirmation ? '人工确认优先；成功日志和正常退出不会自动结束。' : '按启用的完成方式和成功日志判断。' }}</small></div><fieldset><legend>完成方式</legend><div class="signals"><label><input v-model="task.allow_manual_confirmation" type="checkbox" :disabled="busy" @change="save">人工确认</label><label><input v-model="task.allow_process_exit" type="checkbox" :disabled="busy" @change="save">正常退出</label></div><details class="custom-rules"><summary>添加自定义规则</summary><div class="signals"><label>自定义成功<input :value="task.success_log || ''" :disabled="busy" @change="setText(task, 'success_log', $event)"><select v-model="task.success_log_mode" :disabled="busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label><label>自定义失败<input :value="task.failure_log || ''" :disabled="busy" @change="setText(task, 'failure_log', $event)"><select v-model="task.failure_log_mode" :disabled="busy" @change="save"><option value="keyword">关键字</option><option value="regex">正则</option></select></label></div></details></fieldset></section>

      <section v-if="isMaa()" class="editor-section"><div class="section-heading"><div><h3>安卓模拟器连接</h3></div><small>只做 ADB 连通预检，不改写 MAA 或模拟器配置。</small></div><div class="grid"><label class="path">ADB 程序<div><input v-model="ensureMaa(task).adb_executable" placeholder="adb.exe" :disabled="busy" @change="save"><button :disabled="busy" @click="chooseStandalone('adb', task)">选择 ADB</button></div></label><label>连接地址<input v-model="ensureMaa(task).adb_address" placeholder="例如 127.0.0.1:5555" :disabled="busy" @change="save"></label></div><div class="adapter-result" :class="{ good: ensureMaa(task).last_check_ok, bad: ensureMaa(task).last_check_ok === false }"><span>{{ ensureMaa(task).last_check_message || '尚未检测安卓模拟器连接。' }}</span><small v-if="ensureMaa(task).last_check_at">最近检测：{{ ensureMaa(task).last_check_at }}</small><button :disabled="busy" @click="checkAdb(task)">检测连接</button></div></section>
      <section v-if="isMaaEnd()" class="editor-section"><div class="section-heading"><div><h3>Windows 游戏启动编排</h3></div><small>仅管理本轮由本应用创建的 Windows 游戏进程。</small></div><div class="grid"><label class="path">游戏 EXE<div><input v-model="ensureMaaEnd(task).game_executable" placeholder="可留空：直接启动 MaaEnd" :disabled="busy" @change="save"><button :disabled="busy" @click="chooseStandalone('game', task)">选择游戏</button></div></label><label>游戏启动参数<input :value="ensureMaaEnd(task).game_arguments.join(' ')" :disabled="busy" @change="setArgs(task, $event, 'game_arguments')"></label><label>启动后等待（秒）<input v-model.number="ensureMaaEnd(task).game_wait_seconds" min="0" max="3600" type="number" :disabled="busy" @change="save"></label><label class="checkbox-label"><input v-model="ensureMaaEnd(task).close_game_on_finish" type="checkbox" :disabled="busy" @change="save">MaaEnd 结束后关闭本轮启动的游戏</label></div></section>
      <section v-if="task.preset_id" class="editor-section"><div class="section-heading"><div><h3>预设自动规则</h3></div><small>模板提供，只读展示。</small></div><section class="preset-rule-panel"><b>{{ presetLabel(task) }} 模板规则</b><p>{{ presetFor(task)?.note }}</p><dl><dt>日志文件</dt><dd>{{ task.log_path || '无稳定日志终态，使用人工确认。' }}</dd><dt>模板成功规则</dt><dd>{{ presetRuleText(task, 'success') }}</dd><dt>模板失败规则</dt><dd>{{ presetRuleText(task, 'failure') }}</dd><dt>证据</dt><dd>{{ task.preset_evidence || presetFor(task)?.evidence }}</dd></dl></section></section>
      <section class="editor-section danger-zone"><div class="section-heading"><div><h3>危险操作</h3></div><small>删除后无法恢复当前软件配置。</small></div><button class="danger" :disabled="busy" @click="remove(script)">删除当前软件脚本</button></section>
    </section>
  </section>
</template>

<style scoped>
.editor-preset-step>label{display:grid;gap:7px;max-width:620px;color:#b6cbd6;font-size:14px}.editor-preset-step select{font-size:15px}.editor-run-config .grid{grid-template-columns:repeat(3,minmax(0,1fr))}@media(max-width:980px){.editor-run-config .grid{grid-template-columns:1fr}}
</style>
