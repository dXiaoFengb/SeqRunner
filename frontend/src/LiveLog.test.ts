import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import LiveLog from './LiveLog.vue'

test('follows new logs only while at the bottom and resumes on request', async () => {
  const wrapper = mount(LiveLog, { props: { logs: ['第一条'] } })
  const list = wrapper.get('.side-live-list').element as HTMLElement
  Object.defineProperties(list, { scrollHeight: { configurable: true, value: 500 }, clientHeight: { configurable: true, value: 100 } })
  list.scrollTop = 100; await wrapper.get('.side-live-list').trigger('scroll')
  await wrapper.setProps({ logs: ['第一条', '任务成功，来源：CompletionSource.MANUAL'] })
  expect(list.scrollTop).toBe(100); expect(wrapper.text()).toContain('来源：人工确认')
  await wrapper.get('button').trigger('click'); expect(list.scrollTop).toBe(500)
  await wrapper.setProps({ logs: ['新消息'] }); expect(list.scrollTop).toBe(500)
})
