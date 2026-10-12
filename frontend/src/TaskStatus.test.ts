import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import TaskStatus from './TaskStatus.vue'

test('renders a supplied runtime detail', () => {
  expect(mount(TaskStatus, { props: { state: 'failed', detail: '日志失败' } }).text()).toBe('日志失败')
})
