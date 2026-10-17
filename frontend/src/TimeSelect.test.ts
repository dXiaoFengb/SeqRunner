import { mount } from '@vue/test-utils'
import { expect, test, vi } from 'vitest'
import TimeSelect from './TimeSelect.vue'

test('time menu supports keyboard and does not scroll on pointer hover', async () => {
  const scroll = vi.fn()
  const previous = HTMLElement.prototype.scrollIntoView
  HTMLElement.prototype.scrollIntoView = scroll
  const wrapper = mount(TimeSelect, { attachTo: document.body, props: { value: '09', options: ['08', '09', '10'], unit: '时', label: '小时', disabled: false } })
  await wrapper.get('button').trigger('click')
  expect(scroll).toHaveBeenCalledTimes(1)
  const options = document.querySelectorAll('.time-menu [role="option"]')
  options[2].dispatchEvent(new Event('pointermove'))
  expect(scroll).toHaveBeenCalledTimes(1)
  await wrapper.get('button').trigger('keydown', { key: 'ArrowUp' })
  await wrapper.get('button').trigger('keydown', { key: 'Enter' })
  expect(wrapper.emitted('change')).toBeUndefined()
  await wrapper.get('button').trigger('click')
  await wrapper.get('button').trigger('keydown', { key: 'ArrowDown' })
  await wrapper.get('button').trigger('keydown', { key: 'Enter' })
  expect(wrapper.emitted('change')).toEqual([['10']])
  await wrapper.setProps({ disabled: true })
  expect(wrapper.get('button').attributes('disabled')).toBeDefined()
  wrapper.unmount()
  HTMLElement.prototype.scrollIntoView = previous
})
