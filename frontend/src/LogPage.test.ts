import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import LogPage from './LogPage.vue'
import type { RunItem } from './types'

type TestRun = { id: string; queue_name: string; trigger_source: string; state: string; started_at: string; items: RunItem[]; scheduler_skips: string[] }

const run = (id: string, started_at: string): TestRun => ({ id, queue_name: id, trigger_source: 'manual', state: 'completed', started_at, items: [], scheduler_skips: [] })
const fixture = [run('新年', '2026-01-01T12:00:00'), run('边界', '2025-12-26T12:00:00'), run('旧记录', '2025-12-25T12:00:00')]
const setup = (history: TestRun[] = fixture, selectedId: string | null = null) => mount(LogPage, { props: { history, selectedId, logs: ['实时信息'], expanded: new Set<string>() } })
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date(2026, 0, 1, 12)) })
afterEach(() => { vi.useRealTimers() })

test('defaults to inclusive week, groups by local day and keeps live logs separate', () => {
  const wrapper = setup()
  expect((wrapper.get('[aria-label="开始日期"]').element as HTMLInputElement).value).toBe('2025-12-26')
  expect(wrapper.text()).toContain('新年'); expect(wrapper.text()).toContain('边界'); expect(wrapper.text()).not.toContain('旧记录')
  expect(wrapper.findAll('.log-group')).toHaveLength(2); expect(wrapper.text()).toContain('实时信息')
})

test('presets, search, invalid range and reset retain applied results correctly', async () => {
  const wrapper = setup()
  await wrapper.findAll('.quick-ranges button').find(b => b.text() === '昨天')!.trigger('click')
  expect((wrapper.get('[aria-label="开始日期"]').element as HTMLInputElement).value).toBe('2025-12-31')
  await wrapper.findAll('.filter-actions button')[0].trigger('click')
  expect(wrapper.text()).toContain('没有符合条件的日志'); expect(wrapper.text()).toContain('实时信息')
  await wrapper.get('[aria-label="开始日期"]').setValue('2026-02-01'); await wrapper.get('[aria-label="结束日期"]').setValue('2026-01-01')
  await wrapper.findAll('.filter-actions button')[0].trigger('click')
  expect(wrapper.text()).toContain('开始日期不能晚于结束日期'); expect(wrapper.text()).toContain('没有符合条件的日志')
  await wrapper.findAll('.filter-actions button')[1].trigger('click')
  expect(wrapper.text()).toContain('新年'); expect(wrapper.text()).toContain('边界'); expect(wrapper.text()).not.toContain('旧记录')
})

test('calendar month calculation clamps short months and all quick ranges are available', async () => {
  vi.setSystemTime(new Date(2026, 2, 31, 12)); const wrapper = setup()
  for (const [label, expected] of [['今天','2026-03-31'], ['昨天','2026-03-30'], ['最近一周','2026-03-25'], ['最近一个月','2026-02-28'], ['最近两个月','2026-01-31'], ['最近三个月','2025-12-31'], ['最近半年','2025-09-30']]) {
    await wrapper.findAll('.quick-ranges button').find(b => b.text() === label)!.trigger('click')
    expect((wrapper.get('[aria-label="开始日期"]').element as HTMLInputElement).value).toBe(expected)
  }
})

test('focused failed record outside the range becomes visible, with skips and details', async () => {
  const failed = { ...run('旧失败', '2025-01-01T12:00:00'), items: [{ task_id:'t', task_name:'任务', state:'failed', error_summary:'失败原因', log_summary:['摘要'] }], scheduler_skips: ['计划跳过信息'] }
  const wrapper = setup([failed], '旧失败')
  await wrapper.setProps({ expanded: new Set(['旧失败']) })
  expect(wrapper.text()).toContain('旧失败'); expect(wrapper.text()).toContain('失败原因'); expect(wrapper.text()).toContain('计划跳过信息'); expect(wrapper.text()).toContain('摘要')
})

test('distinguishes no records from no matching records', () => {
  expect(setup([]).text()).toContain('暂无运行记录')
  expect(setup([run('旧', '2020-01-01T12:00:00')]).text()).toContain('没有符合条件的日志')
})

test('custom range includes both end dates and groups multiple runs on one day', async () => {
  const wrapper = setup([run('上界', '2026-01-01T23:59:00'), run('同日', '2026-01-01T01:00:00'), run('下界', '2025-12-25T00:00:00'), run('界外', '2025-12-24T23:59:00')])
  await wrapper.get('[aria-label="开始日期"]').setValue('2025-12-25')
  await wrapper.get('[aria-label="结束日期"]').setValue('2026-01-01')
  await wrapper.findAll('.filter-actions button')[0].trigger('click')
  expect(wrapper.findAll('.log-group')).toHaveLength(2)
  expect(wrapper.findAll('.log-group')[0].findAll('.run-record')).toHaveLength(2)
  expect(wrapper.text()).toContain('下界'); expect(wrapper.text()).not.toContain('界外')
})
