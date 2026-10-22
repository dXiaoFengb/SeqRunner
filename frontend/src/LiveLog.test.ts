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

test('labels stages with color classes and reports unread entries while manual scrolling is paused', async () => {
  const wrapper = mount(LiveLog, { props: { logs: [{ message: '开始校验任务配置', phase: 'validation' }, { message: '正在启动程序', phase: 'running' }, { message: '等待人工确认', phase: 'waiting' }, { message: '准备收尾', phase: 'closing' }, { message: '固定冷却', phase: 'cooling' }, { message: '任务成功', phase: 'success' }, { message: '任务已停止', phase: 'failed' }] } })
  const list = wrapper.get('.scheduler-live-list').element as HTMLElement
  Object.defineProperties(list, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  await nextTick(); await nextTick()
  expect(wrapper.get('.log-phase-validation').text()).toContain('校验')
  expect(wrapper.get('.log-phase-running').text()).toContain('运行')
  expect(wrapper.get('.log-phase-waiting').text()).toContain('等待确认')
  expect(wrapper.get('.log-phase-cooling').text()).toContain('冷却')
  expect(wrapper.get('.log-phase-success').text()).toContain('完成')
  expect(wrapper.get('.log-phase-failed').text()).toContain('失败')
  list.scrollTop = 100; await wrapper.get('.scheduler-live-list').trigger('scroll')
  await wrapper.setProps({ logs: [...wrapper.props('logs'), { message: '收尾完成', phase: 'closing' }] })
  expect(wrapper.text()).toContain('回到最新（1）')
  expect(wrapper.get('.log-phase-closing').text()).toContain('收尾')
})

test('distinguishes a script with a configured log source and shows the final elapsed time', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', started_at: '2026-01-01T00:00:00Z', ended_at: null, items: [{ task_id: 't', task_name: 'MAA', state: 'waiting_manual', log_source_configured: true }] }
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

test('keeps the scheduler above a configured script log area throughout a run', async () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: 'MAA', state: 'monitoring', log_source_configured: true }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: [], runtime } })
  const scheduler = wrapper.get('.scheduler-live-list').element
  const script = wrapper.get('.script-live-list').element
  expect(scheduler.compareDocumentPosition(script) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  await wrapper.setProps({ runtime: { ...runtime, activeTaskId: null } })
  expect(wrapper.get('.scheduler-live-list').element).toBe(scheduler)
  expect(wrapper.find('.script-live-list').exists()).toBe(false)
})

test('hides the script log panel with no configured source while keeping software logs', () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: '无日志脚本', state: 'monitoring' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: ['本软件仍在运行'], runtime } })
  expect(wrapper.find('.script-live-list').exists()).toBe(false)
  expect(wrapper.get('.scheduler-live-list').text()).toContain('本软件仍在运行')
})

test('shows the script log panel for an enabled manual region even when OCR is still locating', () => {
  const run = { id: 'r', queue_name: '队列', state: 'running', items: [{ task_id: 't', task_name: '已框选脚本', state: 'monitoring', ui_log_source_configured: true, ui_log_status: '正在定位画面识别（可能有错字或遗漏）' }] }
  const runtime = { busy: true, activeTaskId: 't', cooldownSeconds: 0, run }
  const wrapper = mount(LiveLog, { props: { logs: ['本软件仍在运行'], runtime } })
  expect(wrapper.get('.script-live-list').text()).toContain('正在定位画面识别')
  expect(wrapper.get('.scheduler-live-list').text()).toContain('本软件仍在运行')
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
