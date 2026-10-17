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

test('compacts after content scroll even when a run appears after mount', async () => {
  const empty = { ...structuredClone(runtime), busy: false, run: null, runCenter: null }
  const wrapper = mount(RunCenter, { props: { runtime: empty, countdown: 0, confirmationPending: false, scrolled: false } })
  await wrapper.setProps({ runtime, scrolled: true })
  expect(wrapper.find('.run-center').classes()).toContain('is-compact')
  const expand = wrapper.get('.run-center-expand')
  await expand.trigger('click')
  expect(expand.attributes('aria-expanded')).toBe('true')
  expect(wrapper.find('.run-center').classes()).not.toContain('is-compact')
  await wrapper.setProps({ runtime: empty })
  expect(wrapper.find('.run-center').exists()).toBe(false)
})
