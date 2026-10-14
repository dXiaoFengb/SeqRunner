import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, test, vi } from 'vitest'
import App from './App.vue'
import type { Config, Runtime } from './types'

function task(id: string, name: string, executable = '') {
  return { id, name, executable, arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword' as const, failure_log: null, failure_log_mode: 'keyword' as const, success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600, maa_adapter: null, maaend_adapter: null }
}
function config(two = false): Config {
  const first = task('t1', '第一软件')
  const result: Config = { schema_version: 3, fired_slots: [], startup_fired_dates: {}, plans: [], queues: [{ id: 's1', name: '第一软件', task_ids: ['t1'], time_slots: [] }], tasks: [first] }
  if (two) { result.tasks.push(task('t2', '第二软件')); result.queues.push({ id: 's2', name: '第二软件', task_ids: ['t2'], time_slots: [] }) }
  return result
}
class QuietWebSocket { static readonly CONNECTING = 0; static readonly OPEN = 1; readyState = 0; onopen = null; onmessage = null; onclose = null; onerror = null; constructor(_: string) {} close() {} send() {} }
function installBackend(source = config(), runtime: Runtime = { busy: false, activeTaskId: null, cooldownSeconds: 0, run: null }) {
  const fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
    const path = String(url)
    if (path.endsWith('/health')) return { ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }
    if (path.endsWith('/runtime')) return { ok: true, json: async () => runtime, text: async () => '' }
    if (path.endsWith('/history')) return { ok: true, json: async () => ({ runs: [] }), text: async () => '' }
    if (path.endsWith('/presets')) return { ok: true, json: async () => [{ id: 'maa', label: 'MAA', executables: ['MAA.exe'], note: '日志成功', evidence: 'evidence', manualRequired: true, requiresLogRules: true, successRules: [{ pattern: '完成', mode: 'keyword' }], failureRules: [{ pattern: '失败', mode: 'keyword' }] }, { id: 'maaend', label: 'MaaEnd', executables: ['MaaEnd.exe'], note: '人工确认', evidence: 'evidence', manualRequired: true, editorCapability: 'maaend_windows_game' }], text: async () => '' }
    if (path.includes('/api/presets/')) { const body = JSON.parse(String(options?.body)); const id = path.includes('/maaend/') ? 'maaend' : 'maa'; return { ok: true, json: async () => ({ ...source.tasks[0], executable: body.executable, working_directory: 'C:\\Apps', name: id === 'maa' ? 'MAA' : 'MaaEnd', preset_id: id, preset_evidence: 'evidence', allow_manual_confirmation: true, allow_process_exit: false, success_log_rules: id === 'maa' ? [{ pattern: '完成', mode: 'keyword' }] : [], failure_log_rules: id === 'maa' ? [{ pattern: '失败', mode: 'keyword' }] : [], maa_adapter: id === 'maa' ? { installation_directory: 'C:\\Apps', adb_executable: null, adb_address: null, last_check_at: null, last_check_ok: null, last_check_message: null } : null, maaend_adapter: id === 'maaend' ? { installation_directory: 'C:\\Apps', game_executable: null, game_arguments: [], game_wait_seconds: 60, close_game_on_finish: true } : null }), text: async () => '' } }
    if (path.endsWith('/config')) return { ok: true, json: async () => options?.method === 'PUT' ? JSON.parse(String(options.body)) : structuredClone(source), text: async () => '' }
    if (path.endsWith('/adb-check')) return { ok: true, json: async () => ({ ok: false, message: '模拟器未就绪', checkedAt: '2026-10-14T00:00:00Z' }), text: async () => '' }
    return { ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }
  })
  vi.stubGlobal('fetch', fetchMock); vi.stubGlobal('WebSocket', QuietWebSocket); return fetchMock
}
function runner() { window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }), selectDirectory: async () => ({ canceled: true }) }; return mount(App) }
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

test('keeps script cards free from timing controls and makes plans the timing entry', async () => {
  installBackend(config(true)); const wrapper = runner(); await flushPromises()
  expect(wrapper.text()).not.toContain('添加定时槽')
  await wrapper.findAll('button').find((button) => button.text() === '计划')!.trigger('click')
  expect(wrapper.text()).toContain('计划队列')
  await wrapper.findAll('button').find((button) => button.text().includes('新建计划'))!.trigger('click')
  expect(wrapper.text()).toContain('计划选择')
  expect(wrapper.text()).toContain('定时列表')
})

test('shows only Android emulator connection controls for an MAA script', async () => {
  installBackend(); const wrapper = runner(); await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  await wrapper.find('.editor-run-config select').setValue('maa'); await flushPromises()
  expect(wrapper.text()).toContain('MAA 安装目录')
  expect(wrapper.text()).toContain('安卓模拟器连接')
  expect(wrapper.text()).toContain('ADB 程序')
  expect(wrapper.text()).not.toContain('Windows 游戏启动编排')
  expect(wrapper.text()).toContain('模板成功规则')
  expect(wrapper.text()).toContain('添加自定义规则')
})

test('shows Windows game orchestration only for MaaEnd', async () => {
  installBackend(); const wrapper = runner(); await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  await wrapper.find('.editor-run-config select').setValue('maaend'); await flushPromises()
  expect(wrapper.text()).toContain('MaaEnd 安装目录')
  expect(wrapper.text()).toContain('Windows 游戏启动编排')
  expect(wrapper.text()).toContain('MaaEnd 结束后关闭本轮启动的游戏')
  expect(wrapper.text()).not.toContain('安卓模拟器连接')
})

test('keeps a completed failure visible and opens history detail', async () => {
  const failed = { task_id: 't1', task_name: '第一软件', script_id: 's1', script_name: '第一软件', state: 'failed', error_summary: '日志失败' }
  installBackend(config(), { busy: false, activeTaskId: null, cooldownSeconds: 0, runCenter: null, run: { id: 'r1', queue_name: '第一软件', state: 'completed', items: [failed] } })
  const wrapper = runner(); await flushPromises()
  expect(wrapper.text()).toContain('失败项')
  await wrapper.findAll('button').find((button) => button.text() === '查看失败详情')!.trigger('click')
  expect(wrapper.text()).toContain('运行历史')
})
