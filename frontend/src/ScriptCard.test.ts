import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import ScriptCard from './ScriptCard.vue'
import type { Task } from './types'

const task: Task = { id: 't', name: '启动器', executable: '', arguments: [], working_directory: null, allow_manual_confirmation: true, allow_process_exit: true, log_path: null, success_log: null, success_log_mode: 'keyword', failure_log: null, failure_log_mode: 'keyword', success_log_rules: [], failure_log_rules: [], preset_id: null, preset_evidence: null, timeout_seconds: 600 }
const props = { script: { id: 'q', name: '晨间脚本', task_ids: ['t'], time_slots: [] }, task, locked: false, blocked: false, running: false, presetSummary: '通用程序' }

test('keeps the homepage card compact and exposes keyboard order actions in the menu', async () => {
  const wrapper = mount(ScriptCard, { props })
  expect(wrapper.find('.script-emblem').text()).toBe('通')
  expect(wrapper.find('.script-preset').text()).toBe('通用程序')
  expect(wrapper.text()).toContain('启动器')
  expect(wrapper.text()).not.toContain('个程序')
  expect(wrapper.text()).not.toContain('上移')
  expect(wrapper.text()).not.toContain('程序摘要')
  await wrapper.find('button.run-button').trigger('click')
  expect(wrapper.emitted('run')).toHaveLength(1)
  wrapper.unmount()

  const menuCard = mount(ScriptCard, { props, attachTo: document.body })
  await menuCard.find('.more-trigger').trigger('click')
  await menuCard.vm.$nextTick()
  await document.querySelector('[role="menuitem"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  expect(menuCard.emitted('move')?.[0]).toEqual([-1])
  menuCard.unmount()
})

test('uses a top-layer more menu that can be dismissed with Escape', async () => {
  const wrapper = mount(ScriptCard, { props, attachTo: document.body })
  await wrapper.find('.more-trigger').trigger('click')
  expect(document.body.textContent).toContain('复制脚本')
  await document.querySelector('[role="menu"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  await wrapper.vm.$nextTick()
  expect(document.body.querySelector('[role="menu"]')).toBeNull()
  wrapper.unmount()
})
