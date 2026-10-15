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

test('uses a compact queue selector, four scheduling settings and keeps the title rename in the card header', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  expect(wrapper.text()).toContain('计划选择')
  expect(wrapper.text()).toContain('循环队列（下一版本提供）')
  expect(wrapper.findAll('.queue-settings-grid > label')).toHaveLength(5)
  expect(wrapper.find('.queue-selector-items button').text()).toBe('早班队列')
  expect(wrapper.find('.queue-title .rename-button').exists()).toBe(true)
  expect(wrapper.find('.queue-name').exists()).toBe(false)
})

test('renames a plan from the header pencil without a second name field', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  await wrapper.get('.rename-button').trigger('click')
  const input = wrapper.get('input[aria-label="计划名称"]')
  await input.setValue('晚班队列')
  await input.trigger('keydown.enter')
  expect(data.plans[0].name).toBe('晚班队列')
  expect(wrapper.emitted('save')).toHaveLength(1)
})

test('creates an empty draft and only shows an empty task list after the first time slot', async () => {
  const data = config()
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: null, busy: false } })
  await wrapper.get('button.primary').trigger('click')
  const plan = data.plans[0]
  expect(plan.items).toEqual([])
  await wrapper.setProps({ selectedPlanId: plan.id })
  await wrapper.findAll('button').find((button) => button.text().includes('添加定时'))!.trigger('click')
  expect(wrapper.text()).toContain('任务列表')
  expect(wrapper.text()).toContain('尚未选择软件脚本')
  expect(wrapper.text()).toContain('选择已编辑并保存的软件脚本。')
  expect(wrapper.find('.day-picker').exists()).toBe(true)
  expect(wrapper.find('.time-picker').text()).toContain('时')
  expect(wrapper.text()).not.toContain('上移')
  expect(wrapper.text()).not.toContain('下移')
})

test('adds an unselected row before persisting a chosen script', async () => {
  const data = config(true)
  data.plans[0].time_slots.push({ id: 'slot', enabled: true, days: [0], time: '09:00' })
  data.plans[0].items = []
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.get('button.task-adder').trigger('click')
  expect(wrapper.text()).toContain('未选择脚本')
  expect(data.plans[0].items).toEqual([])
  await wrapper.get('select[aria-label="选择软件脚本"]').setValue('one')

  expect(data.plans[0].items).toHaveLength(1)
  expect(data.plans[0].items[0].script_id).toBe('one')
  expect(wrapper.emitted('save')).toHaveLength(1)
})

test('keeps the last selected task day and uses a whole-row drag source', async () => {
  const data = config(true)
  data.plans[0].items[0].days = [0]
  data.plans[0].time_slots.push({ id: 'slot', enabled: true, days: [0], time: '09:00' })
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  const row = wrapper.find('article.task-row')

  expect(row.attributes('draggable')).toBe('true')
  await row.find('input[type="checkbox"]').setValue(false)

  expect(data.plans[0].items[0].days).toEqual([0])
  expect(wrapper.text()).toContain('请至少选择一个运行日。')
})
