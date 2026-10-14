import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, test, vi } from 'vitest'
import App from './App.vue'
import type { Runtime } from './types'

function makeConfig() {
  return { schema_version: 1 as const, fired_slots: [], queues: [{ id: 'q', name: '测试脚本', task_ids: ['t'], time_slots: [] }], tasks: [{ id: 't', name: '测试程序', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 }] }
}

function makeTwoProgramConfig() {
  const config = makeConfig()
  config.tasks.push({ ...config.tasks[0], id: 't-2', name: '第二程序' })
  config.queues[0].task_ids.push('t-2')
  return config
}

class QuietWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  readyState = QuietWebSocket.CONNECTING
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onclose: (() => void) | null = null
  onerror: (() => void) | null = null
  constructor(_: string) {}
  close() {}
  send() {}
}

function installBackend(config = makeConfig(), runtime: Runtime = { busy: false, activeTaskId: null, cooldownSeconds: 0, run: null }, history: { runs: Array<Record<string, unknown>> } = { runs: [] }) {
  const fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
    const path = String(url)
    if (path.endsWith('/health')) return { ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }
    if (path.endsWith('/runtime')) return { ok: true, json: async () => runtime, text: async () => '' }
    if (path.endsWith('/history')) return { ok: true, json: async () => history, text: async () => '' }
    if (path.endsWith('/presets')) return { ok: true, json: async () => [{ id: 'maa', label: 'MAA', executables: ['MAA.exe'], note: '日志成功', evidence: 'evidence', manualRequired: true, successRules: [{ pattern: '完成', mode: 'keyword' }], failureRules: [{ pattern: '失败', mode: 'keyword' }] }], text: async () => '' }
    if (path.includes('/api/presets/maa/apply')) {
      const body = JSON.parse(String(options?.body))
      return { ok: true, json: async () => ({ ...config.tasks[0], executable: body.executable, working_directory: 'C:\\MAA', preset_id: 'maa', preset_evidence: 'evidence', allow_manual_confirmation: true, allow_process_exit: false, success_log_rules: [{ pattern: '完成', mode: 'keyword' }] }), text: async () => '' }
    }
    if (path.endsWith('/config')) {
      if (options?.method === 'PUT') {
        const accepted = JSON.parse(String(options.body))
        return { ok: true, json: async () => accepted, text: async () => '' }
      }
      return { ok: true, json: async () => structuredClone(config), text: async () => '' }
    }
    return { ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }
  })
  vi.stubGlobal('fetch', fetchMock)
  vi.stubGlobal('WebSocket', QuietWebSocket)
  return fetchMock
}

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

test('shows compact script workbench and moves diagnostics to settings', async () => {
  installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  expect(wrapper.text()).toContain('脚本管理')
  expect(wrapper.text()).not.toContain('连接诊断')
  await wrapper.findAll('button').find((button) => button.text() === '设置')!.trigger('click')
  expect(wrapper.text()).toContain('连接诊断')
})

test('manual matching executable applies and displays preset rules', async () => {
  const fetchMock = installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  await wrapper.find('select').setValue('maa')
  await flushPromises()
  const executable = wrapper.find('input[placeholder="请选择 .exe"]')
  await executable.setValue('C:\\MAA\\MAA.exe')
  await executable.trigger('change')
  await flushPromises()
  expect((wrapper.find('input[placeholder="请选择 .exe"]').element as HTMLInputElement).value).toBe('C:\\MAA\\MAA.exe')
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/api/presets/maa/apply'))).toBe(true)
  expect(wrapper.text()).toContain('MAA 模板规则')
  expect(wrapper.text()).toContain('脚本配置已保存')
})

test('keeps a mismatched executable visible and blocks running until explicitly handled', async () => {
  installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: false, path: 'C:\\Other\\Other.exe', matchesPreset: false }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  await wrapper.find('select').setValue('maa')
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '选择软件')!.trigger('click')
  await flushPromises()
  expect((wrapper.find('input[placeholder="请选择 .exe"]').element as HTMLInputElement).value).toBe('C:\\Other\\Other.exe')
  expect(wrapper.text()).toContain('切换为通用程序并保存')
  await wrapper.findAll('button').find((button) => button.text() === '← 返回')!.trigger('click')
  await flushPromises()
  expect(wrapper.find('button.run-button').attributes('disabled')).toBeDefined()
})

test('edits exactly one software without a program selector or add action', async () => {
  installBackend(makeTwoProgramConfig())
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  expect(wrapper.findAll('.program-choice')).toHaveLength(0)
  expect(wrapper.text()).not.toContain('添加程序')
  expect(wrapper.text()).not.toContain('选择要编辑的程序')
  expect((wrapper.find('input[aria-label="软件名称"]').element as HTMLInputElement).value).toBe('测试程序')
})

test('uses one clear editor sheet with explicit save confirmation while preserving automatic save', async () => {
  const fetchMock = installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  expect(wrapper.findAll('.editor-sheet')).toHaveLength(1)
  expect(wrapper.find('.editor-navigation').text()).toContain('脚本管理 / 编辑软件')
  expect(wrapper.find('.content > header').exists()).toBe(false)
  expect(wrapper.text()).toContain('测试程序 脚本配置')
  expect(wrapper.text()).toContain('使用说明')
  expect(wrapper.text()).toContain('基本信息')
  expect(wrapper.text()).toContain('运行配置')
  expect(wrapper.text()).toContain('完成信号')
  await wrapper.findAll('button').find((button) => button.text() === '保存更改')!.trigger('click')
  await flushPromises()
  expect(fetchMock.mock.calls.some(([url, options]) => String(url).endsWith('/config') && options?.method === 'PUT')).toBe(true)
  expect(wrapper.text()).toContain('保存更改已完成')
})

test('uses the two-column basic editor layout and keeps configuration controls dark-styled', async () => {
  installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  expect(wrapper.find('.basic-grid .script-name').exists()).toBe(true)
  expect(wrapper.find('.basic-grid .path input[placeholder="请选择 .exe"]').exists()).toBe(true)
  expect(wrapper.find('.editor-run-config select').exists()).toBe(true)
})

test('organizes settings into application information and connection diagnostics', async () => {
  installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '设置')!.trigger('click')
  expect(wrapper.findAll('.settings-section')).toHaveLength(2)
  expect(wrapper.text()).toContain('应用信息')
  expect(wrapper.text()).toContain('连接诊断')
})

test('maps each software card to its own waiting and terminal run states', async () => {
  const config = makeConfig()
  config.queues.push({ id: 'q-2', name: '第二脚本', task_ids: ['t-2'], time_slots: [] })
  config.tasks.push({ ...config.tasks[0], id: 't-2', name: '第二程序', executable: 'C:\\second.exe' })
  config.tasks[0].executable = 'C:\\first.exe'
  const runtime: Runtime = {
    busy: true,
    activeTaskId: 't-2',
    cooldownSeconds: 0,
    cooldownUntil: null,
    runCenter: { currentScriptId: 'q-2', currentScriptName: '第二脚本', currentTaskId: 't-2', currentTaskName: '第二程序', currentState: 'waiting_manual', completionSource: null, currentIndex: 2, totalPrograms: 2 },
    run: { id: 'run-1', queue_name: '全部脚本', run_mode: 'all_scripts', state: 'running', items: [{ task_id: 't', task_name: '测试程序', script_id: 'q', script_name: '测试脚本', state: 'succeeded', completion_source: 'manual' }, { task_id: 't-2', task_name: '第二程序', script_id: 'q-2', script_name: '第二脚本', state: 'waiting_manual' }] },
  }
  installBackend(config, runtime)
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  const cards = wrapper.findAll('.script-card')
  expect(cards[0].text()).toContain('已完成')
  expect(cards[0].text()).not.toContain('运行中')
  expect(cards[1].text()).toContain('等待确认')
})

test('shows template rules immediately and keeps custom rules separate', async () => {
  installBackend()
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  await wrapper.find('select').setValue('maa')
  await flushPromises()
  expect(wrapper.text()).toContain('模板成功规则')
  expect(wrapper.text()).toContain('关键字：完成')
  expect(wrapper.text()).toContain('添加自定义规则')
  expect(wrapper.text()).not.toContain('手工附加成功')
})

test('keeps a completed failure visible and opens the matching history record', async () => {
  const failedItem = { task_id: 'task-1', task_name: '失败程序', script_id: 'q', script_name: '测试脚本', state: 'failed', error_summary: '日志命中失败标记' }
  const completed = { busy: false, activeTaskId: null, cooldownSeconds: 0, cooldownUntil: null, runCenter: null, run: { id: 'run-failed', queue_name: '测试脚本', state: 'completed', items: [failedItem] } }
  installBackend(makeConfig(), completed, { runs: [{ id: 'run-failed', queue_name: '测试脚本', trigger_source: 'manual', state: 'completed', started_at: '2026-10-13T10:00:00Z', items: [failedItem] }] })
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  expect(wrapper.text()).toContain('失败程序')
  await wrapper.findAll('button').find((button) => button.text() === '查看失败详情')!.trigger('click')
  expect(wrapper.text()).toContain('脚本运行历史')
  expect(wrapper.text()).toContain('日志命中失败标记')
})
