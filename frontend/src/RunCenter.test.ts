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
})

test('emits confirmation only while the active program is waiting', async () => {
  const waiting = structuredClone(runtime)
  waiting.runCenter!.currentState = 'waiting_manual'
  const wrapper = mount(RunCenter, { props: { runtime: waiting, countdown: 0, confirmationPending: false } })
  await wrapper.get('button.confirm').trigger('click')
  expect(wrapper.emitted('confirm')?.[0]).toEqual(['task-1'])
})
