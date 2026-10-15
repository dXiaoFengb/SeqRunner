import { mount } from '@vue/test-utils'
import { expect, test } from 'vitest'
import PlanManager from './PlanManager.vue'
import type { Config, Plan } from './types'

function plan(id: string, name: string): Plan {
  return { id, name, items: [{ id: `${id}-item`, script_id: 'one', days: [0, 1, 2, 3, 4, 5, 6] }], time_slots: [], startup_mode: 'never', scheduled_enabled: true, completion_action: 'none', completion_delay_minutes: 0, script_ids: [], run_on_startup: false }
}

function config(withPlan = false): Config {
  const base: Config = {
    schema_version: 3, fired_slots: [], startup_fired_dates: {}, tasks: [],
    queues: [{ id: 'one', name: '软件一', task_ids: [], time_slots: [] }, { id: 'two', name: '软件二', task_ids: [], time_slots: [] }],
    plans: [],
  }
  if (withPlan) base.plans.push(plan('plan', '早班队列'))
  return base
}

test('shows the centered no-plan state without a plan selector', () => {
  const wrapper = mount(PlanManager, { props: { config: config(), selectedPlanId: null, busy: false } })
  expect(wrapper.text()).toContain('暂无计划')
  expect(wrapper.text()).toContain('还没有创建任何计划')
  expect(wrapper.text()).not.toContain('计划选择')
  expect(wrapper.find('.plan-card-rail').exists()).toBe(false)
})

test('uses selectable plan cards rather than an independent selector and keeps title rename in the editor header', () => {
  const data = config(true)
  data.plans.push(plan('plan-two', '晚班队列'))
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  expect(wrapper.text()).not.toContain('计划选择')
  expect(wrapper.findAll('.plan-card')).toHaveLength(2)
  expect(wrapper.findAll('.plan-card-select')[1].text()).toContain('晚班队列')
  expect(wrapper.find('.queue-title .rename-button').exists()).toBe(true)
  expect(wrapper.find('.queue-name').exists()).toBe(false)
  expect(wrapper.findAll('.queue-settings-grid > label')).toHaveLength(5)
})

test('switches and keyboard-sorts plan cards while preserving their own schedules', async () => {
  const data = config(true)
  data.plans[0].time_slots.push({ id: 'morning', enabled: true, days: [0], time: '09:00' })
  const second = plan('plan-two', '晚班队列')
  second.time_slots.push({ id: 'evening', enabled: true, days: [0], time: '18:00' })
  data.plans.push(second)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.findAll('.plan-card-select')[1].trigger('click')
  expect(wrapper.emitted('update:selectedPlanId')?.at(-1)).toEqual(['plan-two'])
  await wrapper.findAll('.plan-card-handle')[0].trigger('keydown', { key: 'ArrowDown', altKey: true })

  expect(data.plans.map((item) => item.id)).toEqual(['plan-two', 'plan'])
  expect(data.plans.find((item) => item.id === 'plan')?.time_slots[0].time).toBe('09:00')
  expect(data.plans.find((item) => item.id === 'plan-two')?.time_slots[0].time).toBe('18:00')
  expect(wrapper.emitted('save')).toHaveLength(1)
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
  const created = data.plans[0]
  expect(created.items).toEqual([])
  await wrapper.setProps({ selectedPlanId: created.id })
  await wrapper.findAll('button').find((button) => button.text().includes('添加定时'))!.trigger('click')
  expect(wrapper.text()).toContain('任务列表')
  expect(wrapper.text()).toContain('尚未选择软件脚本')
  expect(wrapper.text()).toContain('选择已编辑并保存的软件脚本。')
  expect(wrapper.findAll('.weekday-tag')).toHaveLength(7)
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

test('shows default all-day tags, a dark checked panel, and protects the last selected day', async () => {
  const data = config(true)
  data.plans[0].items[0].days = [0]
  data.plans[0].time_slots.push({ id: 'slot', enabled: true, days: [0, 1, 2, 3, 4, 5, 6], time: '09:00' })
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  const slotSummary = wrapper.findAll('.weekday-summary')[0]

  expect(slotSummary.findAll('.weekday-tag')).toHaveLength(7)
  expect(slotSummary.text()).toContain('周一 ×')
  await slotSummary.trigger('click')
  expect(wrapper.find('.weekday-popover').exists()).toBe(true)
  expect(wrapper.findAll('.weekday-popover button.selected')).toHaveLength(7)

  const taskTag = wrapper.findAll('.weekday-summary')[1].find('.weekday-tag')
  await taskTag.trigger('click')
  expect(data.plans[0].items[0].days).toEqual([0])
  expect(wrapper.text()).toContain('请至少选择一个运行日。')
})
