import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, test, vi } from 'vitest'
import App from './App.vue'

const config = { schema_version: 1, fired_slots: [], queues: [{ id: 'q', name: '测试队列', task_ids: ['t'], time_slots: [] }], tasks: [{ id: 't', name: '测试任务', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 }] }

afterEach(() => vi.unstubAllGlobals())

test('shows script workbench and native executable selection', async () => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => ({ ok: true, json: async () => url.endsWith('/config') ? config : url.endsWith('/runtime') ? { busy: false, activeTaskId: null, cooldownSeconds: 0, run: null } : url.endsWith('/history') ? { runs: [] } : [{ id: 'maa', label: 'MAA', executables: ['MAA.exe'], note: '日志成功', evidence: 'evidence', manualRequired: true }], text: async () => '' })))
  vi.stubGlobal('WebSocket', class { onmessage: ((event: MessageEvent) => void) | null = null; onclose: (() => void) | null = null; constructor(_: string) {} close() {} })
  window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:1', wsBase: 'ws://127.0.0.1:1' }), selectExecutable: async () => ({ canceled: true }) }
  const wrapper = mount(App)
  await flushPromises()
  expect(wrapper.text()).toContain('脚本管理')
  expect(wrapper.text()).toContain('新建脚本')
  await wrapper.findAll('button').find((button) => button.text() === '编辑脚本')!.trigger('click')
  expect(wrapper.text()).toContain('选择软件')
  expect(wrapper.text()).toContain('计划')
})
