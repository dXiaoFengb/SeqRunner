<script setup lang="ts">
import { computed, ref } from 'vue'
import { BellRing, DatabaseBackup, Download, Info, ServerCog, Settings2 } from '@lucide/vue'

type AppSettings = { autoStart: boolean; minimizeOnLaunch: boolean; notifications: { completed: boolean; failed: boolean } }
type SettingsChange = { autoStart?: boolean; minimizeOnLaunch?: boolean; notifications?: Partial<AppSettings['notifications']> }
type SettingsTab = 'function' | 'notifications' | 'logs' | 'about'
type PresetOption = { id: string; label: string }

const props = defineProps<{
  settings: AppSettings
  saveState: 'idle' | 'saving' | 'saved' | 'failed'
  version: string
  apiOnline: boolean
  realtimeLabel: string
  diagnosticText: string
  presets: PresetOption[]
  saveSettings: (value: AppSettings) => Promise<void>
  exportArchive: (kind: 'backup' | 'logs' | 'diagnostic', presetId?: string) => Promise<{ path?: string; canceled?: boolean }>
  refreshDiagnostics: () => Promise<void>
}>()

const tab = ref<SettingsTab>('function')
const actionNote = ref('')
const tabs: Array<{ id: SettingsTab; label: string; icon: typeof Settings2 }> = [
  { id: 'function', label: '功能设置', icon: Settings2 },
  { id: 'notifications', label: '通知设置', icon: BellRing },
  { id: 'logs', label: '日志管理', icon: DatabaseBackup },
  { id: 'about', label: '关于', icon: Info },
]
const activeTab = computed(() => tabs.find((item) => item.id === tab.value) || tabs[0])
const tabSummary: Record<SettingsTab, string> = {
  function: '管理应用启动行为，并查看受管后端的运行边界。',
  notifications: '只在一轮任务出现明确结果时提醒。',
  logs: '导出受管数据与本体日志，不采集外部软件原始日志。',
  about: '查看版本、连接状态与本地诊断。',
}
const saveLabel = computed(() => props.saveState === 'saving' ? '正在保存…' : props.saveState === 'saved' ? '已保存' : props.saveState === 'failed' ? '保存失败，请重试' : '')

function nextSettings(change: SettingsChange) {
  return { ...props.settings, ...change, notifications: { ...props.settings.notifications, ...change.notifications } }
}
async function update(change: SettingsChange) {
  actionNote.value = ''
  try { await props.saveSettings(nextSettings(change)) } catch (error) { actionNote.value = error instanceof Error ? error.message : String(error) }
}
async function exportPackage(kind: 'backup' | 'logs' | 'diagnostic', presetId?: string) {
  actionNote.value = ''
  try {
    const result = await props.exportArchive(kind, presetId)
    actionNote.value = result.canceled ? '已取消导出。' : result.path ? `已导出：${result.path}` : '导出完成。'
  } catch (error) { actionNote.value = error instanceof Error ? `导出失败：${error.message}` : '导出失败。' }
}
</script>

<template>
  <section class="settings-workbench" aria-label="设置中心">
    <nav class="settings-tabs" role="tablist" aria-label="设置功能选项卡">
      <button v-for="item in tabs" :id="`settings-tab-${item.id}`" :key="item.id" :class="{ active: tab === item.id }" role="tab" :aria-selected="tab === item.id" :aria-controls="`settings-panel-${item.id}`" @click="tab = item.id"><component :is="item.icon" aria-hidden="true" /><span>{{ item.label }}</span></button>
    </nav>
    <section class="settings-stage" role="tabpanel" :aria-labelledby="`settings-tab-${tab}`" :id="`settings-panel-${tab}`">
      <header class="settings-stage-heading"><div><h2>{{ activeTab.label }}</h2><p>{{ tabSummary[tab] }}</p></div><small v-if="saveLabel" :class="`settings-save-${saveState}`">{{ saveLabel }}</small></header>

      <template v-if="tab === 'function'">
        <section class="settings-section"><div class="settings-section-heading"><h3>启动配置</h3><p>更改会保存到本机，并在下一次打开应用时生效。</p></div><div class="settings-option-list"><label class="settings-option"><span><b>开机自启</b><small>登录 Windows 后自动启动顺序任务调度器。</small></span><input type="checkbox" :checked="settings.autoStart" aria-label="开机自启" @change="update({ autoStart: ($event.target as HTMLInputElement).checked })"></label><label class="settings-option"><span><b>启动后直接最小化</b><small>打开应用后最小化到任务栏，不影响后台运行。</small></span><input type="checkbox" :checked="settings.minimizeOnLaunch" aria-label="启动后直接最小化" @change="update({ minimizeOnLaunch: ($event.target as HTMLInputElement).checked })"></label></div></section>
        <section class="settings-section"><div class="settings-section-heading"><h3>后端运行方式</h3><p>应用负责启动、监视、停止和进程清理，避免出现无人管理的后台进程。</p></div><div class="settings-runtime"><ServerCog aria-hidden="true" /><div><b>自动</b><small>由应用 Runtime 监督；暂不支持手动接管。</small></div><span>受管</span></div></section>
        <p class="settings-note">软件脚本仍按原路径读取已授权的外部日志；设置中心不修改外部软件配置，也不批量采集外部日志。</p>
      </template>

      <template v-else-if="tab === 'notifications'">
        <section class="settings-section"><div class="settings-section-heading"><h3>Windows 通知</h3><p>仅在一轮运行结束、失败或停止时提醒，运行过程不会产生打扰通知。</p></div><div class="settings-option-list"><label class="settings-option"><span><b>运行结束</b><small>所有软件结束且没有失败项时通知。</small></span><input type="checkbox" :checked="settings.notifications.completed" aria-label="运行结束通知" @change="update({ notifications: { completed: ($event.target as HTMLInputElement).checked } })"></label><label class="settings-option"><span><b>失败或停止</b><small>有失败项或用户停止本轮时通知。</small></span><input type="checkbox" :checked="settings.notifications.failed" aria-label="失败或停止通知" @change="update({ notifications: { failed: ($event.target as HTMLInputElement).checked } })"></label></div></section>
      </template>

      <template v-else-if="tab === 'logs'">
        <section class="settings-section settings-export-section"><div class="settings-section-heading"><h3>数据备份</h3><p>导出本软件的配置、运行历史、迁移备份和设置文件。</p></div><button class="primary" @click="exportPackage('backup')"><Download aria-hidden="true" />导出数据备份 ZIP</button></section>
        <section class="settings-section settings-export-section"><div class="settings-section-heading"><h3>本体日志导出</h3><p>导出本软件的后端日志、应用明细日志和导出清单。</p></div><button class="primary" @click="exportPackage('logs')"><Download aria-hidden="true" />导出本体日志 ZIP</button></section>
        <section class="settings-section"><div class="settings-section-heading"><h3>专项问题包导出</h3><p>按预设生成仅含本软件受管数据的诊断包；不包含目标软件外部日志。</p></div><div class="diagnostic-actions"><button v-for="preset in presets" :key="preset.id" @click="exportPackage('diagnostic', preset.id)"><Download aria-hidden="true" />{{ preset.label }} 问题包</button><button v-if="!presets.length" disabled>暂无可导出的预设问题包</button></div></section>
      </template>

      <template v-else>
        <section class="settings-section"><div class="settings-section-heading"><h3>应用信息</h3></div><dl class="settings-facts"><dt>开发版本</dt><dd>{{ version }}</dd><dt>数据目录</dt><dd>开发模式使用根目录 data\；便携版使用 EXE 同级 UserData\。</dd><dt>隐私边界</dt><dd>外部软件日志按原路径读取，不复制或删除。</dd></dl></section>
        <section class="settings-section"><div class="settings-section-heading"><h3>连接诊断</h3></div><dl class="settings-facts"><dt>API 状态</dt><dd>{{ apiOnline ? '在线：保存与运行可用' : '离线：等待后端恢复' }}</dd><dt>实时状态</dt><dd>{{ realtimeLabel }}</dd></dl><div class="settings-diagnostics-actions"><button @click="refreshDiagnostics">刷新主进程诊断</button><details class="connection-diagnostics"><summary>查看主进程诊断详情</summary><code>{{ diagnosticText }}</code></details></div></section>
      </template>
      <p v-if="actionNote" class="settings-action-note" role="status">{{ actionNote }}</p>
    </section>
  </section>
</template>
