import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import { nextTick } from 'vue'
import LiveLog from './LiveLog.vue'
import type { Runtime, ScriptReadableEvent } from './types'

function runtime(taskId = 'task'): Runtime {
  return { busy: true, activeTaskId: taskId, cooldownSeconds: 0, runCenter: { currentScriptId: null, currentScriptName: null, currentTaskId: taskId, currentTaskName: 'MAA', currentState: 'monitoring', completionSource: null, currentIndex: 1, totalPrograms: 1, readableProgress: '正在监控程序运行' }, run: { id: 'run', queue_name: '队列', state: 'running', items: [{ task_id: taskId, task_name: 'MAA', state: 'monitoring', log_source_configured: true, script_translation_version: 'script-local-rules-v1' }] } }
}

function scriptEvent(sequence: number, message = '脚本正在加载配置。'): ScriptReadableEvent {
  return { runId: 'run', taskId: 'task', sequence, truncated: false, event: { timestamp: '2026-01-01T00:00:00Z', task_id: 'task', task_name: 'MAA', source: 'script', sequence, phase: '脚本日志', severity: sequence === 2 ? 'warning' : 'info', event_type: 'script.log.translated', message, rule_id: 'script.module.configurationhelper' } }
}

test('always follows new software-status logs even after manual scrolling', async () => {
  const wrapper = mount(LiveLog, { props: { logs: ['第一条'] } })
  const list = wrapper.get('.scheduler-live-list').element as HTMLElement
  Object.defineProperties(list, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  await nextTick(); await nextTick()
  list.scrollTop = 100
  await wrapper.setProps({ logs: ['第一条', '任务成功，来源：CompletionSource.MANUAL'] })
  await nextTick()
  expect(list.scrollTop).toBe(500)
  expect(wrapper.get('[aria-label="本软件运行状态"]').text()).toContain('本软件运行状态')
  expect(wrapper.text()).not.toContain('回到最新')
})

test('renders the merged translated script stream without source selectors and follows new events', async () => {
  const current = runtime()
  const wrapper = mount(LiveLog, { props: { logs: ['正在启动程序'], runtime: current, scriptEvents: [scriptEvent(1)] } })
  const list = wrapper.get('.script-live-list').element as HTMLElement
  Object.defineProperties(list, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  await nextTick(); await nextTick()
  list.scrollTop = 100
  await wrapper.setProps({ scriptEvents: [scriptEvent(1), scriptEvent(2, '脚本记录了警告级信息；请查看脚本内部日志确认原文。')] })
  await nextTick()
  expect(wrapper.get('.script-header').text()).toContain('脚本实时转译')
  expect(wrapper.get('.script-live-list').text()).toContain('脚本记录了警告级信息')
  expect(wrapper.find('.script-severity-warning').exists()).toBe(true)
  expect(wrapper.find('.script-source-tabs').exists()).toBe(false)
  expect(list.scrollTop).toBe(500)
})

test('shows a folded translated event count without restoring source selectors', async () => {
  const folded = scriptEvent(1)
  folded.event = { ...folded.event, occurrence_count: 4, last_timestamp: '2026-01-01T00:00:03Z' }
  const wrapper = mount(LiveLog, { props: { logs: [], runtime: runtime(), scriptEvents: [folded] } })
  expect(wrapper.get('.script-live-list').text()).toContain('×4')
  expect(wrapper.find('.script-source-tabs').exists()).toBe(false)
})

test('keeps the script translation panel hidden without an external file source while software status remains', () => {
  const current = runtime()
  current.run!.items[0].log_source_configured = false
  const wrapper = mount(LiveLog, { props: { logs: ['本软件仍在运行'], runtime: current } })
  expect(wrapper.find('.script-live-list').exists()).toBe(false)
  expect(wrapper.get('.scheduler-live-list').text()).toContain('本软件仍在运行')
})

test('shows only converted events for the active task and resets on task switch', async () => {
  const current = runtime()
  const otherBase = scriptEvent(2, '其他脚本事件')
  const other = { ...otherBase, taskId: 'other', event: { ...otherBase.event, task_id: 'other' } }
  const wrapper = mount(LiveLog, { props: { logs: [], runtime: current, scriptEvents: [scriptEvent(1), other] } })
  expect(wrapper.get('.script-live-list').text()).not.toContain('其他脚本事件')
  const next = runtime('other')
  next.run!.items = [{ task_id: 'other', task_name: '下一程序', state: 'monitoring', log_source_configured: true, script_translation_version: 'script-local-rules-v1' }]
  await wrapper.setProps({ runtime: next, scriptEvents: [other] })
  expect(wrapper.get('.script-live-list').text()).toContain('其他脚本事件')
})
