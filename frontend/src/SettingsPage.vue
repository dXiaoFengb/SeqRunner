<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { BellRing, DatabaseBackup, Download, Info, ServerCog, Settings2 } from '@lucide/vue'

type AppSettings = { autoStart: boolean; minimizeOnLaunch: boolean; notifications: { completed: boolean; failed: boolean; system: boolean } }
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
const qqState = ref<{ bound: boolean; enabled: boolean; state: string }>({ bound:false, enabled:false, state:'unbound' })
const qr = ref<{ id:string; image:string } | null>(null)
const qqNote = ref('')
const confirmUnbind = ref(false)
const qqDialog = ref(false)
const qrView = ref(false)
const qrBusy = ref(false)
const qqDialogElement = ref<HTMLElement | null>(null)
let qqOpener: HTMLElement | null = null
let pollTimer: ReturnType<typeof setTimeout> | undefined
let pollGeneration = 0
const notifyMode = computed(() => props.settings.notifications.completed ? (props.settings.notifications.failed ? 'all' : 'legacy-success') : (props.settings.notifications.failed ? 'failed' : 'none'))
async function changeNotifyMode(value: string) { const values: Record<string, { completed:boolean; failed:boolean }> = { none:{completed:false,failed:false}, all:{completed:true,failed:true}, failed:{completed:false,failed:true}, 'legacy-success':{completed:true,failed:false} }; if (values[value]) await update({ notifications:values[value] }) }
async function refreshQQ() { try { if (window.desktopRunner?.qqStatus) qqState.value = await window.desktopRunner.qqStatus() } catch (error) { qqNote.value = error instanceof Error ? error.message : '无法读取 QQ 状态。' } }
async function cancelQR() { ++pollGeneration; if (pollTimer) clearTimeout(pollTimer); qr.value = null; await window.desktopRunner?.qqCancel?.(); await refreshQQ() }
async function startQR() { if (qrBusy.value) return; qrView.value = true; qrBusy.value = true; try { await cancelQR(); if (!qqDialog.value || !qrView.value) return; const generation = ++pollGeneration; if (!window.desktopRunner?.qqStart) throw new Error('QQ 绑定服务不可用。'); qqNote.value = '正在生成二维码…'; const result = await window.desktopRunner.qqStart(); if (generation !== pollGeneration || !qqDialog.value) return; qr.value = result; qqNote.value = '请使用手机 QQ 扫描二维码并确认。'; const poll = async () => { try { const state = await window.desktopRunner!.qqPoll!(result.id); if (generation !== pollGeneration) return; if (state.state === 'expired') { qr.value = null; qqNote.value = '二维码已过期，请重新生成。'; return } if (state.state === 'connecting') { qr.value = null; qrView.value = false; qqNote.value = 'QQ 已绑定，正在连接消息网关。'; await refreshQQ(); return } qqNote.value = state.state === 'scanned' ? '已扫码，请在手机上确认。' : '请使用手机 QQ 扫描二维码并确认。'; pollTimer = setTimeout(poll, 2000) } catch (error) { if (generation === pollGeneration) { await cancelQR(); qqNote.value = error instanceof Error ? error.message : 'QQ 绑定失败。' } } }; pollTimer = setTimeout(poll, 2000) } catch (error) { if (qqDialog.value) qqNote.value = error instanceof Error ? error.message : 'QQ 绑定失败。' } finally { qrBusy.value = false } }
async function openQQ(event: Event) { qqOpener = event.currentTarget as HTMLElement; qqDialog.value = true; qrView.value = false; confirmUnbind.value = false; qqNote.value = ''; await refreshQQ(); await nextTick(); qqDialogElement.value?.focus() }
async function closeQQ() { const cancel = qrView.value; qqDialog.value = false; qrView.value = false; confirmUnbind.value = false; if (cancel) await cancelQR(); await nextTick(); if (qqOpener?.isConnected) qqOpener.focus(); else document.querySelector<HTMLElement>('[data-qq-channel]')?.focus() }
async function backFromQR() { await cancelQR(); qrView.value = false; qqNote.value = '' }
function qqKeydown(event: KeyboardEvent) { if (event.key === 'Escape') { event.preventDefault(); if (confirmUnbind.value) confirmUnbind.value = false; else void closeQQ(); return } if (event.key !== 'Tab' || !qqDialogElement.value) return; const controls = Array.from(qqDialogElement.value.querySelectorAll<HTMLElement>('button:not([disabled]), select:not([disabled]), input:not([disabled]), [href]')); if (!controls.length) { event.preventDefault(); return } const first = controls[0], last = controls[controls.length - 1]; if (event.shiftKey && (document.activeElement === first || document.activeElement === qqDialogElement.value)) { event.preventDefault(); last.focus() } else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() } }
async function enableQQ(value: boolean) { try { await window.desktopRunner?.qqEnable?.(value); await refreshQQ() } catch (error) { qqNote.value = error instanceof Error ? error.message : 'QQ 设置失败。' } }
async function unbindQQ() { try { await window.desktopRunner?.qqUnbind?.(); confirmUnbind.value = false; await refreshQQ(); qqNote.value = 'QQ 已解绑。' } catch (error) { qqNote.value = error instanceof Error ? error.message : 'QQ 解绑失败。' } }
async function testQQ() { try { if (!window.desktopRunner?.qqTest) throw new Error('QQ 通知服务不可用。'); await window.desktopRunner.qqTest(); qqNote.value = 'QQ 测试通知已发送。' } catch (error) { qqNote.value = error instanceof Error ? error.message : 'QQ 测试通知失败。' } }
let statusTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => { void refreshQQ(); statusTimer = setInterval(() => { if (tab.value === 'notifications') void refreshQQ() }, 3000) })
onBeforeUnmount(() => { ++pollGeneration; if (pollTimer) clearTimeout(pollTimer); if (statusTimer) clearInterval(statusTimer); if (qrView.value) void window.desktopRunner?.qqCancel?.() })
type WebhookSummary = { id: string; name: string; enabled: boolean; method: 'GET' | 'POST' }
type WebhookDraft = { id?: string; name: string; enabled: boolean; method: 'GET' | 'POST'; url: string; template: string; headersText: string }
const hooks = ref<WebhookSummary[]>([])
const hookDraft = ref<WebhookDraft | null>(null)
const deleteId = ref<string | null>(null)
const hookBusy = ref(false)
const hooksUnreadable = ref(false)
const resetConfirmation = ref(false)
const hookError = ref('')
const confirmError = ref('')
const confirmBusy = ref(false)
const hookInitial = ref('')
const discardHook = ref(false)
const hookDialogElement = ref<HTMLElement | null>(null)
const confirmDialogElement = ref<HTMLElement | null>(null)
let hookOpener: HTMLElement | null = null
let confirmOpener: HTMLElement | null = null
function rememberOpener(event: Event) { hookOpener = event.currentTarget as HTMLElement }
async function focusHook() { await nextTick(); hookDialogElement.value?.focus() }
async function openConfirmation(kind: 'delete' | 'reset', event: Event, id?: string) {
  confirmError.value = ''; confirmOpener = event.currentTarget as HTMLElement
  if (kind === 'delete') deleteId.value = id || null
  else resetConfirmation.value = true
  await nextTick(); confirmDialogElement.value?.querySelector<HTMLElement>('[data-cancel]')?.focus()
}
async function closeConfirmation() { if (confirmBusy.value) return; deleteId.value = null; resetConfirmation.value = false; confirmError.value = ''; await nextTick(); if (confirmOpener?.isConnected) confirmOpener.focus(); else document.querySelector<HTMLElement>('[data-add-hook]')?.focus() }
async function closeHook() { hookDraft.value = null; discardHook.value = false; hookError.value = ''; await nextTick(); hookOpener?.focus() }
async function requestCloseHook() {
  if (hookBusy.value) return
  if (JSON.stringify(hookDraft.value) !== hookInitial.value) { discardHook.value = true; await nextTick(); confirmDialogElement.value?.querySelector<HTMLElement>('[data-cancel]')?.focus() }
  else await closeHook()
}
async function keepEditing() { discardHook.value = false; await nextTick(); hookDialogElement.value?.focus() }
function dialogKeydown(event: KeyboardEvent, dialog: HTMLElement | null, close: () => void) {
  if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); return }
  if (event.key !== 'Tab' || !dialog) return
  const controls = Array.from(dialog.querySelectorAll<HTMLElement>('button:not([disabled]), select:not([disabled]), input:not([disabled]), textarea:not([disabled]), summary, [href]')).filter(el => el.getClientRects().length > 0)
  if (!controls.length) { event.preventDefault(); return }
  const first = controls[0], last = controls[controls.length - 1]
  if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
}
async function loadHooks(): Promise<boolean> { try { hooks.value = await window.desktopRunner?.listWebhooks?.() || []; hooksUnreadable.value = false; return true } catch (error) { hooksUnreadable.value = true; actionNote.value = error instanceof Error ? error.message : '无法读取 Webhook 配置。'; return false } }
async function resetHooks() {
  if (confirmBusy.value) return
  confirmBusy.value = true; confirmError.value = ''
  try { if (!window.desktopRunner?.resetUnreadableWebhooks) throw new Error('Webhook 服务不可用。'); await window.desktopRunner.resetUnreadableWebhooks(); confirmBusy.value = false; await closeConfirmation(); if (await loadHooks()) actionNote.value = '已保留无法读取的加密文件，请重新添加 Webhook。' }
  catch (error) { confirmError.value = error instanceof Error ? error.message : '无法重新配置。' } finally { confirmBusy.value = false }
}
onMounted(loadHooks)
async function newHook(event: Event) { rememberOpener(event); hookError.value = ''; hookDraft.value = { name: '', enabled: false, method: 'POST', url: '', template: '{"title":"{title}","content":"{content}"}', headersText: '{}' }; hookInitial.value = JSON.stringify(hookDraft.value); await focusHook() }
async function editHook(id: string, event: Event) { rememberOpener(event); hookError.value = ''; try { const item = await window.desktopRunner?.getWebhook?.(id); if (!item) throw new Error('Webhook 服务不可用。'); hookDraft.value = { ...item, headersText: JSON.stringify(item.headers, null, 2) }; hookInitial.value = JSON.stringify(hookDraft.value); await focusHook() } catch (error) { actionNote.value = error instanceof Error ? error.message : '无法读取 Webhook。' } }
async function saveHook() {
  if (!hookDraft.value || hookBusy.value) return
  hookBusy.value = true; hookError.value = ''
  try {
    const { headersText, ...item } = hookDraft.value
    let headers: unknown
    try { headers = JSON.parse(headersText) } catch { throw new Error('请求头必须是有效的 JSON 对象。') }
    if (!headers || typeof headers !== 'object' || Array.isArray(headers)) throw new Error('请求头必须是 JSON 对象。')
    if (!window.desktopRunner?.saveWebhook) throw new Error('Webhook 服务不可用。')
    await window.desktopRunner.saveWebhook({ ...item, headers: headers as Record<string, string> })
    await closeHook(); if (await loadHooks()) actionNote.value = 'Webhook 已保存。'
  } catch (error) { hookError.value = error instanceof Error ? error.message : '保存失败。' } finally { hookBusy.value = false }
}
async function removeHook() {
  if (!deleteId.value || confirmBusy.value) return
  confirmBusy.value = true; confirmError.value = ''
  try { if (!window.desktopRunner?.removeWebhook) throw new Error('Webhook 服务不可用。'); await window.desktopRunner.removeWebhook(deleteId.value); const loaded = await loadHooks(); confirmBusy.value = false; await closeConfirmation(); if (loaded) actionNote.value = 'Webhook 已删除。' }
  catch (error) { confirmError.value = error instanceof Error ? error.message : '删除失败。' } finally { confirmBusy.value = false }
}
async function toggleHook(item: WebhookSummary, enabled: boolean) { try { const full = await window.desktopRunner?.getWebhook?.(item.id); if (!full || !window.desktopRunner?.saveWebhook) throw new Error('Webhook 服务不可用。'); await window.desktopRunner.saveWebhook({ ...full, enabled }); if (await loadHooks()) actionNote.value = 'Webhook 状态已保存。' } catch (error) { actionNote.value = error instanceof Error ? error.message : '保存失败。' } }
async function testHook(id: string) { hookBusy.value = true; try { if (!window.desktopRunner?.testWebhook) throw new Error('Webhook 服务不可用。'); await window.desktopRunner.testWebhook(id); actionNote.value = '测试通知已发送。' } catch (error) { actionNote.value = error instanceof Error ? error.message : '测试通知发送失败。' } finally { hookBusy.value = false } }
async function testSystem() { try { const result = await window.desktopRunner?.testSystemNotification?.(); actionNote.value = result?.shown ? '系统测试通知已发送。' : '当前系统不支持通知。' } catch { actionNote.value = '系统测试通知发送失败。' } }
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
        <section class="settings-section settings-card" data-testid="settings-function-block"><div class="settings-section-heading"><h3>启动配置</h3><p>更改会保存到本机，并在下一次打开应用时生效。</p></div><div class="settings-option-list"><label class="settings-option"><span><b>开机自启</b><small>登录 Windows 后自动启动顺序任务调度器。</small></span><select :value="String(settings.autoStart)" aria-label="开机自启" @change="update({ autoStart: ($event.target as HTMLSelectElement).value === 'true' })"><option value="false">否</option><option value="true">是</option></select></label><label class="settings-option"><span><b>启动后直接最小化</b><small>打开应用后最小化到任务栏，不影响后台运行。</small></span><select :value="String(settings.minimizeOnLaunch)" aria-label="启动后直接最小化" @change="update({ minimizeOnLaunch: ($event.target as HTMLSelectElement).value === 'true' })"><option value="false">否</option><option value="true">是</option></select></label></div></section>
        <section class="settings-section settings-card" data-testid="settings-function-block"><div class="settings-section-heading"><h3>后端运行方式</h3><p>应用负责启动、监视、停止和进程清理，避免出现无人管理的后台进程。</p></div><div class="settings-runtime"><ServerCog aria-hidden="true" /><div><b>自动</b><small>由应用 Runtime 监督；暂不支持手动接管。</small></div><span>受管</span></div></section>
        <p class="settings-note">软件脚本仍按原路径读取已授权的外部日志；设置中心不修改外部软件配置，也不批量采集外部日志。</p>
      </template>

      <template v-else-if="tab === 'notifications'">
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>通知内容</h3><p>运行结束后按所选条件向启用的渠道发送。</p></div><label class="notify-policy">推送任务结果时机<select :value="notifyMode" aria-label="推送任务结果时机" @change="changeNotifyMode(($event.target as HTMLSelectElement).value)"><option value="none">不推送</option><option value="all">全部结果</option><option value="failed">仅失败或停止</option><option v-if="notifyMode === 'legacy-success'" value="legacy-success">仅成功（原设置）</option></select></label></section>
        <div class="notify-actions"><button v-if="settings.notifications.system" @click="testSystem">测试系统通知</button><button v-if="qqState.enabled" @click="testQQ">测试 QQ 通知</button></div>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>内置渠道 · 已启用 {{ Number(settings.notifications.system) + Number(qqState.enabled) }} 个</h3></div><p v-if="!settings.notifications.system && !qqState.enabled">还没有启用的渠道，可在下方选择渠道进行配置。</p><div class="notify-channel-list"><div v-if="settings.notifications.system" class="webhook-row"><b>系统通知</b><span>已启用</span><button @click="update({ notifications:{ system:false } })">停用</button></div><button v-if="qqState.enabled" data-qq-channel class="qq-channel" @click="openQQ"><b>QQ 官方机器人</b><span>{{ qqState.state === 'connected' ? '已连接' : qqState.state === 'failed' ? '连接失败' : '正在连接' }}</span></button></div></section>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>内置渠道 · 未启用</h3></div><div class="notify-channel-list"><div v-if="!settings.notifications.system" class="webhook-row"><b>系统通知</b><button @click="update({ notifications:{ system:true } })">启用</button></div><button v-if="!qqState.enabled" data-qq-channel class="qq-channel" @click="openQQ"><b>QQ 官方机器人</b><span>{{ qqState.bound ? '已绑定，未启用' : '未绑定' }}</span></button><p v-if="settings.notifications.system && qqState.enabled">全部内置渠道已启用。</p></div></section>
        <Teleport to="body"><div v-if="qqDialog" class="qq-dialog-overlay" @click.self="closeQQ"><section ref="qqDialogElement" class="qq-dialog" role="dialog" aria-modal="true" aria-labelledby="qq-dialog-title" tabindex="-1" @keydown="qqKeydown"><header class="qq-dialog-header"><div><h2 id="qq-dialog-title">{{ qrView ? 'QQ 扫码绑定' : 'QQ 官方机器人通知' }}</h2><p>{{ qrView ? '使用手机 QQ 扫描二维码并确认绑定。' : '使用 QQ 官方机器人接收任务通知。' }}</p></div><button aria-label="关闭 QQ 配置" @click="closeQQ">×</button></header><template v-if="qrView"><div class="qq-dialog-qr"><img v-if="qr" class="qq-qr" :src="qr.image" alt="手机 QQ 扫码绑定二维码"><p role="status">{{ qqNote }}</p></div><div class="qq-dialog-actions"><button :disabled="qrBusy" @click="startQR">刷新二维码</button><button @click="backFromQR">取消</button></div></template><template v-else><div class="qq-dialog-option"><div><b>启用通知</b><p>更改立即生效，无需保存。</p></div><select :value="String(qqState.enabled)" aria-label="启用 QQ 通知" :disabled="!qqState.bound" @change="enableQQ(($event.target as HTMLSelectElement).value === 'true')"><option value="false">否</option><option value="true">是</option></select></div><div class="qq-dialog-status"><h3>绑定状态</h3><p>{{ !qqState.bound ? '未绑定' : !qqState.enabled ? '已绑定，未启用' : qqState.state === 'connected' ? '已连接' : qqState.state === 'failed' ? '连接失败，正在重试' : '已绑定，正在连接' }}</p><div class="qq-dialog-actions"><button v-if="!qqState.bound" class="primary" @click="startQR">扫码绑定</button><button v-if="qqState.bound && qqState.enabled" @click="testQQ">发送测试通知</button><button v-if="qqState.bound && !confirmUnbind" @click="confirmUnbind = true">解绑</button></div><div v-if="confirmUnbind" role="alertdialog" aria-label="确认解绑 QQ"><p>解绑后将停止向此 QQ 发送通知，需要重新扫码绑定。</p><div class="qq-dialog-actions"><button @click="unbindQQ">确认解绑</button><button @click="confirmUnbind = false">取消</button></div></div></div><p v-if="qqNote" class="settings-action-note" role="status">{{ qqNote }}</p></template></section></div></Teleport>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>自定义 Webhook</h3><p>已启用 {{ hooks.filter(item => item.enabled).length }} 个；地址和请求头仅加密保存在当前 Windows 账户。</p></div><p v-if="hooksUnreadable">无法读取已有配置。请在原 Windows 账户中恢复，或保留旧加密文件后重新配置。</p><p v-else-if="!hooks.length">尚未添加 Webhook。</p><div v-for="item in hooks" :key="item.id" class="webhook-row"><b>{{ item.name }}</b><span>{{ item.method }} · {{ item.enabled ? '已启用' : '未启用' }}</span><label>启用 <select :value="String(item.enabled)" :aria-label="`${item.name} 启用`" @change="toggleHook(item, ($event.target as HTMLSelectElement).value === 'true')"><option value="false">否</option><option value="true">是</option></select></label><button :disabled="hookBusy" @click="testHook(item.id)">测试</button><button @click="editHook(item.id, $event)">编辑</button><button @click="openConfirmation('delete', $event, item.id)">删除</button></div><button v-if="hooksUnreadable" @click="openConfirmation('reset', $event)">重新配置</button><button v-else data-add-hook @click="newHook">添加 Webhook</button></section>
        <Teleport to="body">
          <div v-if="hookDraft" class="qq-dialog-overlay">
            <section ref="hookDialogElement" class="hook-dialog" role="dialog" aria-modal="true" aria-labelledby="hook-dialog-title" tabindex="-1" @keydown="dialogKeydown($event, hookDialogElement, () => void requestCloseHook())">
              <header class="hook-dialog-header"><div><h2 id="hook-dialog-title">{{ hookDraft.id ? '编辑 Webhook' : '添加 Webhook' }}</h2><p>配置保存后生效，测试通知可在保存后发送。</p></div><button aria-label="关闭 Webhook 配置" :disabled="hookBusy" @click="requestCloseHook">×</button></header>
              <div class="hook-dialog-body webhook-form">
                <h3>基础配置</h3>
                <label>名称<input v-model="hookDraft.name" autocomplete="off"></label>
                <label>Webhook URL<input v-model="hookDraft.url" type="password" autocomplete="off"></label>
                <label>方法<select v-model="hookDraft.method"><option>POST</option><option>GET</option></select></label>
                <label>启用<select v-model="hookDraft.enabled" aria-label="启用 Webhook"><option :value="false">否</option><option :value="true">是</option></select></label>
                <h3>高级配置</h3>
                <label>消息模板<textarea v-model="hookDraft.template" rows="4"></textarea></label><small>支持 {title}、{content}、{datetime}、{date}、{time}。</small>
                <label>请求头（JSON 对象）<textarea v-model="hookDraft.headersText" rows="3"></textarea></label>
                <p v-if="hookError" class="hook-error" role="alert">{{ hookError }}</p>
              </div>
              <footer class="hook-dialog-actions"><button class="primary" :disabled="hookBusy" @click="saveHook">{{ hookBusy ? '正在保存…' : '保存' }}</button><button :disabled="hookBusy" @click="requestCloseHook">取消</button></footer>
            </section>
          </div>
          <div v-if="discardHook || deleteId || resetConfirmation" class="qq-dialog-overlay hook-confirm-overlay">
            <section ref="confirmDialogElement" class="hook-confirm" role="alertdialog" aria-modal="true" aria-labelledby="hook-confirm-title" tabindex="-1" @keydown="dialogKeydown($event, confirmDialogElement, () => discardHook ? void keepEditing() : void closeConfirmation())">
              <h2 id="hook-confirm-title">{{ discardHook ? '放弃未保存的更改？' : deleteId ? '删除 Webhook？' : '重新配置 Webhook？' }}</h2>
              <p>{{ discardHook ? '未保存的内容将丢失。' : deleteId ? '删除后将停止向此地址发送通知，且需要重新添加。' : '将保留无法读取的旧加密文件，并从空列表开始重新配置。' }}</p>
              <p v-if="confirmError && !discardHook" class="hook-error" role="alert">{{ confirmError }}</p>
              <div class="hook-dialog-actions"><button :disabled="confirmBusy" @click="discardHook ? closeHook() : deleteId ? removeHook() : resetHooks()">{{ discardHook ? '放弃更改' : deleteId ? '确认删除' : '确认重新配置' }}</button><button data-cancel :disabled="confirmBusy" @click="discardHook ? keepEditing() : closeConfirmation()">取消</button></div>
            </section>
          </div>
        </Teleport>
      </template>

      <template v-else-if="tab === 'logs'">
        <section class="settings-section settings-card settings-export-section"><div class="settings-section-heading"><h3>数据备份</h3><p>导出本软件的配置、运行历史、迁移备份和设置文件。</p></div><button class="primary" @click="exportPackage('backup')"><Download aria-hidden="true" />导出数据备份 ZIP</button></section>
        <section class="settings-section settings-card settings-export-section"><div class="settings-section-heading"><h3>本体日志导出</h3><p>导出本软件的后端日志、应用明细日志和导出清单。</p></div><button class="primary" @click="exportPackage('logs')"><Download aria-hidden="true" />导出本体日志 ZIP</button></section>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>专项问题包导出</h3><p>按预设生成仅含本软件受管数据的诊断包；不包含目标软件外部日志。</p></div><div class="diagnostic-actions"><button v-for="preset in presets" :key="preset.id" @click="exportPackage('diagnostic', preset.id)"><Download aria-hidden="true" />{{ preset.label }} 问题包</button><button v-if="!presets.length" disabled>暂无可导出的预设问题包</button></div></section>
      </template>

      <template v-else>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>应用信息</h3></div><dl class="settings-facts"><dt>开发版本</dt><dd>{{ version }}</dd><dt>数据目录</dt><dd>开发模式使用根目录 data\；便携版使用 EXE 同级 UserData\。</dd><dt>隐私边界</dt><dd>外部软件日志按原路径读取，不复制或删除。</dd></dl></section>
        <section class="settings-section settings-card"><div class="settings-section-heading"><h3>连接诊断</h3></div><dl class="settings-facts"><dt>API 状态</dt><dd>{{ apiOnline ? '在线：保存与运行可用' : '离线：等待后端恢复' }}</dd><dt>实时状态</dt><dd>{{ realtimeLabel }}</dd></dl><div class="settings-diagnostics-actions"><button @click="refreshDiagnostics">刷新主进程诊断</button><details class="connection-diagnostics"><summary>查看主进程诊断详情</summary><code>{{ diagnosticText }}</code></details></div></section>
      </template>
      <p v-if="actionNote" class="settings-action-note" role="status">{{ actionNote }}</p>
    </section>
  </section>
</template>
