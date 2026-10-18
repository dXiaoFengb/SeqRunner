import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import { nextTick } from 'vue'
import LiveLog from './LiveLog.vue'

test('follows new logs only while at the bottom and resumes on request', async () => {
  const wrapper = mount(LiveLog, { props: { logs: ['第一条'] } })
  const list = wrapper.get('.side-live-list').element as HTMLElement
  Object.defineProperties(list, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  await nextTick(); await nextTick()
  expect(list.scrollTop).toBe(500)
  list.scrollTop = 100; await wrapper.get('.side-live-list').trigger('scroll')
  await wrapper.setProps({ logs: ['第一条', '任务成功，来源：CompletionSource.MANUAL'] })
  expect(list.scrollTop).toBe(100); expect(wrapper.text()).toContain('来源：人工确认')
  await wrapper.get('button').trigger('click'); expect(list.scrollTop).toBe(500)
  await wrapper.setProps({ logs: ['新消息'] }); expect(list.scrollTop).toBe(500)
})

test('distinguishes the running script and shows the final elapsed time', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', started_at: '2026-01-01T00:00:00Z', ended_at: null, items: [{ task_id: 't', task_name: 'MAA', state: 'waiting_manual' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: ['检查完成'], runtime } })
  expect(wrapper.text()).toContain('MAA · 脚本日志')
  expect(wrapper.text()).toContain('暂无法可靠获取')
  expect(wrapper.text()).toContain('检查完成')
  await wrapper.setProps({ runtime: { ...runtime, busy: false, activeTaskId: null, run: { ...run, state: 'completed', ended_at: '2026-01-01T00:01:03Z' } } })
  expect(wrapper.text()).toContain('检查完成')
  expect(wrapper.text()).toContain('总耗时 0 小时 1 分 3 秒')
  expect(wrapper.get('.scheduler-live-list').text().indexOf('检查完成')).toBeLessThan(wrapper.get('.scheduler-live-list').text().indexOf('总耗时'))
})

test('keeps the scheduler above a reserved script area throughout a run', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: 'MAA', state: 'monitoring' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: [], runtime } })
  const scheduler = wrapper.get('.scheduler-live-list').element
  const script = wrapper.get('.script-live-list').element
  expect(scheduler.compareDocumentPosition(script) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  await wrapper.setProps({ runtime: { ...runtime, activeTaskId: null } })
  expect(wrapper.get('.scheduler-live-list').element).toBe(scheduler)
  expect(wrapper.get('.script-live-list').text()).toContain('等待脚本运行')
})

test('script and scheduler logs scroll independently', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: '程序', state: 'monitoring' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: ['检查完成'], runtime, scriptLog: { runId: 'r', taskId: 't', text: '第一条' } } })
  const script = wrapper.get('.script-live-list').element as HTMLElement
  const scheduler = wrapper.get('.scheduler-live-list').element as HTMLElement
  for (const el of [script, scheduler]) Object.defineProperties(el, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  await nextTick(); await nextTick()
  script.scrollTop = 100
  await wrapper.get('.script-live-list').trigger('scroll')
  await wrapper.setProps({ logs: ['检查完成', '新状态'], scriptLog: { runId: 'r', taskId: 't', text: '第一条\n第二条' } })
  await nextTick()
  expect(script.scrollTop).toBe(100)
  expect(scheduler.scrollTop).toBe(500)
  expect(wrapper.get('.script-live-list').text()).toContain('第二条')
  expect(wrapper.get('.scheduler-live-list').text()).toContain('新状态')
})

test('only shows messages for the active run and task', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: '程序', state: 'monitoring' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: [], runtime, scriptLog: { runId: 'other', taskId: 't', text: '其他运行' } } })
  expect(wrapper.text()).not.toContain('其他运行')
  await wrapper.setProps({ scriptLog: { runId: 'r', taskId: 't', text: '脚本日志' } })
  expect(wrapper.text()).toContain('脚本日志')
})
