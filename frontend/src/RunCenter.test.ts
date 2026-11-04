import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import RunCenter from './RunCenter.vue'
import type { Runtime } from './types'

const runtime: Runtime = {
  busy: true,
  activeTaskId: 'task-1',
  cooldownSeconds: 10,
  cooldownUntil: new Date(Date.now() + 10_000).toISOString(),
  runCenter: { currentScriptId: 'script-1', currentScriptName: '早班脚本', currentTaskId: 'task-1', currentTaskName: '启动程序', currentState: 'cooling_down', completionSource: 'manual', currentIndex: 1, totalPrograms: 2 },
  run: { id: 'run-1', queue_name: '早班脚本', state: 'running', items: [] },
}

test('shows a visible cooldown and reports accepted manual confirmation', async () => {
  const wrapper = mount(RunCenter, { props: { runtime, countdown: 9, confirmationPending: true } })
  expect(wrapper.text()).toContain('9 秒')
  expect(wrapper.text()).toContain('确认已接收')
  expect(wrapper.text()).toContain('正在请求程序正常关闭')
  expect(wrapper.find('.run-center').classes()).toContain('is-sticky')
})

test('emits confirmation only while the active program is waiting', async () => {
  const waiting = structuredClone(runtime)
  waiting.runCenter!.currentState = 'waiting_manual'
  const wrapper = mount(RunCenter, { props: { runtime: waiting, countdown: 0, confirmationPending: false } })
  await wrapper.get('button.confirm').trigger('click')
  expect(wrapper.emitted('confirm')?.[0]).toEqual(['task-1'])
})

test('update choices are separate from task completion confirmation', async () => {
  const waiting = structuredClone(runtime)
  waiting.runCenter!.currentState = 'waiting_update'
  waiting.run!.items = [{ task_id: 'task-1', task_name: '程序', state: 'waiting_update', update_message: '更新需要人工处理' }]
  const wrapper = mount(RunCenter, { props: { runtime: waiting, countdown: 0, confirmationPending: false } })
  expect(wrapper.text()).toContain('更新需要人工处理')
  expect(wrapper.find('button.confirm').exists()).toBe(false)
  await wrapper.findAll('button').find(button => button.text() === '继续等待')!.trigger('click')
  await wrapper.findAll('button').find(button => button.text() === '标记失败并继续')!.trigger('click')
  expect(wrapper.emitted('updateDecision')).toEqual([['task-1', 'wait'], ['task-1', 'fail']])
  expect(wrapper.emitted('confirm')).toBeUndefined()
})

test('reports completed runs with a failed final program without a cooldown', () => {
  const finished = structuredClone(runtime)
  finished.busy = false
  finished.cooldownSeconds = 0
  finished.cooldownUntil = null
  finished.run!.state = 'completed'
  finished.run!.items = [{ task_id: 'task-1', task_name: '最后程序', state: 'failed', error_summary: '退出码 3' }]
  finished.runCenter = null
  const wrapper = mount(RunCenter, { props: { runtime: finished, countdown: 0, confirmationPending: false } })
  expect(wrapper.text()).toContain('整轮执行完成，存在失败项')
  expect(wrapper.text()).not.toContain('秒后启动')
  expect(wrapper.find('.run-center').classes()).not.toContain('is-sticky')
})

test('shows the failed program and opens its history detail from the completed run center', async () => {
  const finished = structuredClone(runtime)
  finished.busy = false
  finished.cooldownSeconds = 0
  finished.cooldownUntil = null
  finished.run!.state = 'completed'
  finished.run!.items = [{ task_id: 'task-1', task_name: '失败程序', script_name: '测试脚本', state: 'failed', error_summary: '日志命中失败标记' }]
  finished.runCenter = null
  const wrapper = mount(RunCenter, { props: { runtime: finished, countdown: 0, confirmationPending: false } })
  expect(wrapper.text()).toContain('1/1')
  expect(wrapper.text()).toContain('测试脚本 / 失败程序')
  expect(wrapper.text()).toContain('日志命中失败标记')
  await wrapper.findAll('button').find((button) => button.text() === '查看失败详情')!.trigger('click')
  expect(wrapper.emitted('showFailures')?.[0]).toEqual(['run-1'])
})

test('shows and cancels a pending completion action countdown', async () => {
  const finished = structuredClone(runtime)
  finished.busy = false
  finished.run!.state = 'completed'
  finished.completionAction = { runId: 'run-1', planId: 'plan-1', planName: '早班', action: 'shutdown', status: 'countdown', remainingSeconds: 47 }
  const wrapper = mount(RunCenter, { props: { runtime: finished, countdown: 0, confirmationPending: false } })
  expect(wrapper.text()).toContain('将在 47 秒后关机')
  expect(wrapper.find('.run-center').classes()).toContain('is-sticky')
  await wrapper.findAll('button').find((button) => button.text() === '取消完成后操作')!.trigger('click')
  expect(wrapper.emitted('cancelCompletionAction')).toHaveLength(1)
})

test('keeps one stable run-center layout while runtime state updates', async () => {
  const empty = { ...structuredClone(runtime), busy: false, run: null, runCenter: null }
  const wrapper = mount(RunCenter, { props: { runtime: empty, countdown: 0, confirmationPending: false } })
  await wrapper.setProps({ runtime })
  expect(wrapper.find('.run-center').classes()).not.toContain('is-compact')
  expect(wrapper.find('.run-center-expand').exists()).toBe(false)
  const waiting = structuredClone(runtime)
  waiting.runCenter!.currentState = 'waiting_manual'
  await wrapper.setProps({ runtime: waiting })
  expect(wrapper.find('.run-center').classes()).not.toContain('is-compact')
  await wrapper.setProps({ runtime: empty })
  expect(wrapper.find('.run-center').exists()).toBe(false)
})

test('shows distinct update and completion actions while update monitoring is active', async () => {
  const updating = structuredClone(runtime)
  updating.runCenter!.currentState = 'monitoring'
  updating.run!.items = [{ task_id: 'task-1', task_name: '程序', state: 'monitoring', update_active: true, manual_completion_available: true }]
  const wrapper = mount(RunCenter, { props: { runtime: updating, countdown: 0, confirmationPending: false } })
  await wrapper.findAll('button').find(button => button.text() === '确认更新已处理，继续监控')!.trigger('click')
  await wrapper.findAll('button').find(button => button.text() === '确认当前程序完成')!.trigger('click')
  expect(wrapper.emitted('updateDecision')).toEqual([['task-1', 'handled'], ['task-1', 'complete']])
  await wrapper.setProps({ updatePending: true })
  expect(wrapper.findAll('button').filter(button => button.text().includes('确认')).every(button => button.attributes('disabled') !== undefined)).toBe(true)
})
