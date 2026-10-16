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

async function openEditor(wrapper: ReturnType<typeof mount>) {
  await wrapper.get('.plan-card-actions .primary').trigger('click')
}

test('shows the centered no-plan state and only the header creation entry', () => {
  const wrapper = mount(PlanManager, { props: { config: config(), selectedPlanId: null, busy: false } })
  expect(wrapper.text()).toContain('暂无计划')
  expect(wrapper.text()).toContain('还没有创建任何计划')
  expect(wrapper.find('.plan-overview').exists()).toBe(false)
  expect(wrapper.find('.queue-detail').exists()).toBe(false)
  expect(wrapper.get('button.primary').text()).toContain('新建计划')
})

test('uses independent plan rows with operations bound to their own plan', () => {
  const data = config(true)
  data.plans.push(plan('plan-two', '晚班队列'))
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  expect(wrapper.findAll('.plan-card')).toHaveLength(2)
  expect(wrapper.find('.plan-card-add').exists()).toBe(false)
  expect(wrapper.find('.plan-overview-actions').exists()).toBe(false)
  expect(wrapper.findAll('.plan-card-actions')).toHaveLength(2)
  expect(wrapper.findAll('button[aria-label^="编辑计划："]')).toHaveLength(2)
  expect(wrapper.findAll('button[aria-label^="删除计划："]')).toHaveLength(2)
  expect(wrapper.get('.queue-page-head .primary').text()).toContain('新建计划')
  expect(wrapper.find('.queue-detail').exists()).toBe(false)
})

test('keyboard-sorts plan rows without changing their own schedules', async () => {
  const data = config(true)
  data.plans[0].time_slots.push({ id: 'morning', enabled: true, days: [0], time: '09:00' })
  const second = plan('plan-two', '晚班队列')
  second.time_slots.push({ id: 'evening', enabled: true, days: [0], time: '18:00' })
  data.plans.push(second)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.findAll('.plan-card-handle')[0].trigger('keydown', { key: 'ArrowDown', altKey: true })

  expect(data.plans.map((item) => item.id)).toEqual(['plan-two', 'plan'])
  expect(data.plans.find((item) => item.id === 'plan')?.time_slots[0].time).toBe('09:00')
  expect(data.plans.find((item) => item.id === 'plan-two')?.time_slots[0].time).toBe('18:00')
  expect(wrapper.emitted('save')).toHaveLength(1)
})

test('creates a draft from the header and immediately opens the editor', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.get('.queue-page-head .primary').trigger('click')
  expect(data.plans).toHaveLength(2)
  expect(data.plans[1].items).toEqual([])
  expect(wrapper.find('.queue-detail').exists()).toBe(true)
  expect(wrapper.text()).toContain('返回计划列表')
  expect(wrapper.emitted('update:selectedPlanId')?.at(-1)).toEqual([data.plans[1].id])
})

test('keeps edit-only controls out of the overview and returns to the card list', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  expect(wrapper.text()).not.toContain('队列类型')
  await openEditor(wrapper)
  expect(wrapper.find('.queue-title .rename-button').exists()).toBe(true)
  expect(wrapper.text()).toContain('队列类型')
  expect(wrapper.text()).not.toContain('删除计划')
  await wrapper.get('.back-button').trigger('click')
  expect(wrapper.find('.plan-overview').exists()).toBe(true)
})

test('edits and deletes the matching plan row without a global selected action bar', async () => {
  const data = config(true)
  data.plans.push(plan('plan-two', '晚班队列'))
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.get('button[aria-label="删除计划：晚班队列"]').trigger('click')
  expect(data.plans.map((item) => item.id)).toEqual(['plan'])
  expect(wrapper.find('.plan-overview-actions').exists()).toBe(false)
  expect(wrapper.emitted('save')).toHaveLength(1)
})

test('opens the exact plan named by a row action and returns to empty state after the last deletion', async () => {
  const data = config(true)
  data.plans.push(plan('plan-two', '晚班队列'))
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })

  await wrapper.get('button[aria-label="编辑计划：晚班队列"]').trigger('click')
  expect(wrapper.emitted('update:selectedPlanId')?.at(-1)).toEqual(['plan-two'])
  await wrapper.setProps({ selectedPlanId: 'plan-two' })
  expect(wrapper.get('.queue-title h3').text()).toBe('晚班队列')
  await wrapper.get('.back-button').trigger('click')
  await wrapper.get('button[aria-label="删除计划：晚班队列"]').trigger('click')
  await wrapper.get('button[aria-label="删除计划：早班队列"]').trigger('click')
  expect(wrapper.text()).toContain('暂无计划')
  expect(wrapper.find('.plan-overview').exists()).toBe(false)
})

test('renames in the editor and exposes all five accessible field help tips', async () => {
  const data = config(true)
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  await openEditor(wrapper)
  await wrapper.get('.rename-button').trigger('click')
  const input = wrapper.get('input[aria-label="计划名称"]')
  await input.setValue('晚班队列')
  await input.trigger('keydown.enter')
  expect(data.plans[0].name).toBe('晚班队列')

  expect(wrapper.findAll('.help-tooltip-trigger')).toHaveLength(5)
  const queueHelp = wrapper.get('button[aria-label="查看队列类型说明"]')
  await queueHelp.trigger('focus')
  expect(queueHelp.attributes('aria-expanded')).toBe('true')
  expect(wrapper.find('[role="tooltip"]').text()).toContain('定时队列会在启用的时间槽触发')
  await queueHelp.trigger('keydown.esc')
  expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
})

test('shows task configuration only after a first slot and persists a selected pending script', async () => {
  const data = config(true)
  data.plans[0].items = []
  const wrapper = mount(PlanManager, { props: { config: data, selectedPlanId: 'plan', busy: false } })
  await openEditor(wrapper)
  await wrapper.findAll('button').find((button) => button.text().includes('添加定时'))!.trigger('click')
  expect(wrapper.text()).toContain('尚未选择软件脚本')
  expect(wrapper.findAll('.weekday-tag')).toHaveLength(7)
  await wrapper.get('button.task-adder').trigger('click')
  expect(data.plans[0].items).toEqual([])
  await wrapper.get('select[aria-label="选择软件脚本"]').setValue('one')
  expect(data.plans[0].items[0].script_id).toBe('one')
})
