import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import PlanManager from './PlanManager.vue'
import type { Config, Plan } from './types'

function config(withPlan = false): Config {
  const base: Config = {
    schema_version: 3, fired_slots: [], startup_fired_dates: {}, tasks: [],
    queues: [{ id: 'one', name: '软件一', task_ids: [], time_slots: [] }, { id: 'two', name: '软件二', task_ids: [], time_slots: [] }],
    plans: [],
  }
  if (withPlan) {
    const plan: Plan = { id: 'plan', name: '早班队列', items: [{ id: 'item-one', script_id: 'one', days: [0, 1, 2, 3, 4, 5, 6] }, { id: 'item-two', script_id: 'two', days: [0, 1, 2, 3, 4, 5, 6] }], time_slots: [], startup_mode: 'never', scheduled_enabled: true, completion_action: 'none', completion_delay_minutes: 0, script_ids: [], run_on_startup: false }
    base.plans.push(plan)
  }
  return base
}

test('shows a centered empty state without a bordered configuration reminder', () => {
  const wrapper = mount(PlanManager, { props: { config: config(), selectedPlanId: null, busy: false } })
  expect(wrapper.text()).toContain('暂无计划')
  expect(wrapper.text()).toContain('还没有创建任何计划')
  expect(wrapper.find('.queue-selector').exists()).toBe(false)
})

test('uses a queue selector, four scheduling settings and adds task table after timing exists', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  expect(wrapper.text()).toContain('计划选择')
  expect(wrapper.text()).toContain('循环队列（下一版本提供）')
  expect(wrapper.findAll('.queue-settings-grid > label')).toHaveLength(5)
  expect(wrapper.findAll('h3').some((heading) => heading.text() === '任务列表')).toBe(false)
  await wrapper.findAll('button').find((button) => button.text().includes('添加定时'))!.trigger('click')
  expect(wrapper.text()).toContain('任务列表')
  expect(wrapper.find('.day-picker').exists()).toBe(true)
  expect(wrapper.find('.time-picker').text()).toContain('时')
})
