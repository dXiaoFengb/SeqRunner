import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, test, vi } from 'vitest'
import App from './App.vue'

function makeConfig() {
  return { schema_version: 1 as const, fired_slots: [], queues: [{ id: 'q', name: '测试脚本', task_ids: ['t'], time_slots: [] }], tasks: [{ id: 't', name: '测试程序', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 }] }
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

function installBackend(config = makeConfig()) {
  const fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
    const path = String(url)
    if (path.endsWith('/health')) return { ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }
    if (path.endsWith('/runtime')) return { ok: true, json: async () => ({ busy: false, activeTaskId: null, cooldownSeconds: 0, run: null }), text: async () => '' }
    if (path.endsWith('/history')) return { ok: true, json: async () => ({ runs: [] }), text: async () => '' }
    if (path.endsWith('/presets')) return { ok: true, json: async () => [{ id: 'maa', label: 'MAA', executables: ['MAA.exe'], note: '日志成功', evidence: 'evidence', manualRequired: true }], text: async () => '' }
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
  expect(wrapper.text()).toContain('预设自动规则已应用')
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
  await wrapper.findAll('button').find((button) => button.text().includes('返回脚本管理'))!.trigger('click')
  await flushPromises()
  expect(wrapper.find('button.run-button').attributes('disabled')).toBeDefined()
})
