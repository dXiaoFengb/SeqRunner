import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import LogPage from './LogPage.vue'
import type { RunItem } from './types'

type TestRun = { id: string; queue_name: string; trigger_source: string; state: string; started_at: string; items: RunItem[]; scheduler_skips: string[] }

const run = (id: string, started_at: string): TestRun => ({ id, queue_name: id, trigger_source: 'manual', state: 'completed', started_at, items: [], scheduler_skips: [] })
const fixture = [run('新年', '2026-01-01T12:00:00'), run('边界', '2025-12-26T12:00:00'), run('旧记录', '2025-12-25T12:00:00')]
const setup = (history: TestRun[] = fixture, selectedId: string | null = null) => mount(LogPage, { props: { history, selectedId, expanded: new Set<string>() } })
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(new Date(2026, 0, 1, 12)) })
afterEach(() => { vi.useRealTimers() })

test('defaults to inclusive week, groups by local day and keeps live logs separate', () => {
  const wrapper = setup()
  expect((wrapper.get('[aria-label="开始日期"]').element as HTMLInputElement).value).toBe('2025-12-26')
  expect(wrapper.text()).toContain('新年'); expect(wrapper.text()).toContain('边界'); expect(wrapper.text()).not.toContain('旧记录')
  expect(wrapper.findAll('.log-group')).toHaveLength(2)
})

test('presets, search, invalid range and reset retain applied results correctly', async () => {
  const wrapper = setup()
  await wrapper.findAll('.quick-ranges button').find(b => b.text() === '昨天')!.trigger('click')
  expect((wrapper.get('[aria-label="开始日期"]').element as HTMLInputElement).value).toBe('2025-12-31')
  await wrapper.findAll('.filter-actions button')[0].trigger('click')
  expect(wrapper.text()).toContain('没有符合条件的日志')
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

test('localizes known record labels without changing unknown values', async () => {
  const record = { ...run('样例', '2026-01-01T12:00:00'), trigger_source: 'scheduled', items: [{ task_id: 't', task_name: 'MAA', state: 'succeeded', completion_source: 'manual', log_summary: ['任务成功，来源：CompletionSource.MANUAL', '外部 manual'] }] }
  const wrapper = setup([record]); await wrapper.setProps({ expanded: new Set(['样例']) })
  expect(wrapper.text()).toContain('定时触发 · 已完成'); expect(wrapper.text()).toContain('成功：人工确认'); expect(wrapper.text()).toContain('任务成功，来源：人工确认'); expect(wrapper.text()).toContain('外部 manual')
})

test('sorts records by start time within the same day', () => {
  const wrapper = setup([run('早', '2026-01-01T08:00:00'), run('晚', '2026-01-01T18:00:00')])
  const records = wrapper.findAll('.run-record')
  expect(records[0].text()).toContain('晚'); expect(records[0].text()).toContain('18:00:00')
  expect(records[1].text()).toContain('早'); expect(records[1].text()).toContain('08:00:00')
})

test('keeps equal start times stable and groups across days', () => {
  const wrapper = setup([run('前一天', '2025-12-31T23:59:59'), run('相同一', '2026-01-01T08:00:00'), run('相同二', '2026-01-01T08:00:00')])
  const records = wrapper.findAll('.run-record')
  expect(records.map(record => record.text())).toEqual([expect.stringContaining('相同一'), expect.stringContaining('相同二'), expect.stringContaining('前一天')])
})

test('groups ISO weeks across years and months without merging runs, and resets', async () => {
  const wrapper = setup([run('周日', '2025-12-28T23:59:00'), run('跨年一', '2025-12-29T10:00:00'), run('跨年二', '2026-01-01T10:00:00')])
  await wrapper.get('[aria-label="合并模式"]').setValue('week')
  expect(wrapper.findAll('.log-group').map(group => group.get('h2').text())).toEqual(['2026-W01', '2025-W52'])
  expect(wrapper.findAll('.log-group')[0].findAll('.run-record')).toHaveLength(2)
  expect(wrapper.text()).toContain('2025-12-29 10:00:00')
  await wrapper.get('[aria-label="合并模式"]').setValue('month')
  expect(wrapper.findAll('.log-group').map(group => group.get('h2').text())).toEqual(['2026-01', '2025-12'])
  await wrapper.findAll('.filter-actions button')[1].trigger('click')
  expect((wrapper.get('[aria-label="合并模式"]').element as HTMLSelectElement).value).toBe('day')
})

test('previews and cancels cleanup, then confirms without deleting records', async () => {
  const request = vi.fn(async (path: string) => path.endsWith('/preview') ? { count: 2, ids: ['a', 'b'] } : { count: 2 })
  const wrapper = setup(); await wrapper.setProps({ request })
  await wrapper.get('[aria-label="清理明细截止日期"]').setValue('2025-12-31')
  await wrapper.findAll('button').find(b => b.text() === '清理详细日志')!.trigger('click')
  expect(wrapper.get('[role="alertdialog"]').text()).toContain('2 条')
  await wrapper.findAll('button').find(b => b.text() === '取消')!.trigger('click')
  expect(request).toHaveBeenCalledTimes(1)
  await wrapper.findAll('button').find(b => b.text() === '清理详细日志')!.trigger('click')
  await wrapper.findAll('button').find(b => b.text() === '确认清理')!.trigger('click')
  expect(wrapper.text()).toContain('已清理 2 条详细日志'); expect(wrapper.emitted('refresh')).toHaveLength(1)
})

test('loads detailed log pages in order', async () => {
  const request = vi.fn(async (path: string) => path.endsWith('offset=0') ? { entries: [{ captured_at: '2026-01-01T01:00:00Z', text: '第一条' }], next_offset: 10 } : { entries: [{ captured_at: '2026-01-01T01:01:00Z', text: '第二条' }], next_offset: null })
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', detail_log_id: 'detail' }] }
  const wrapper = setup([record]); await wrapper.setProps({ request, expanded: new Set(['样例']) })
  await wrapper.findAll('button').find(b => b.text() === '查看详细日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('暂无法可靠获取该脚本的界面日志')
  expect(wrapper.get('[role="dialog"]').text()).not.toContain('第一条')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('第一条')
  await wrapper.findAll('button').find(b => b.text() === '加载更多')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('第二条')
  await wrapper.findAll('button').find(b => b.text() === '返回脚本界面日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).not.toContain('第一条')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).not.toContain('未识别的日志')
  expect(wrapper.get('[role="dialog"]').text()).toContain('第一条')
})

test('old records show why interface and raw logs are unavailable', async () => {
  const request = vi.fn()
  const record = { ...run('旧记录', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', detail_log_status: '未配置日志路径' }] }
  const wrapper = setup([record]); await wrapper.setProps({ request, expanded: new Set(['旧记录']) })
  await wrapper.findAll('button').find(b => b.text() === '查看详细日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('暂无法可靠获取该脚本的界面日志')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('未配置日志路径')
  expect(request).not.toHaveBeenCalled()
})

test('shows captured interface logs separately from raw logs', async () => {
  const request = vi.fn(async (path: string) => ({ entries: [{ captured_at: '2026-01-01T01:00:00Z', text: path.includes('/ui?') ? '界面消息' : '原始消息' }], next_offset: null }))
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', detail_log_id: 'raw', ui_log_id: 'ui', ui_log_status: '已采集界面日志' }] }
  const wrapper = setup([record]); await wrapper.setProps({ request, expanded: new Set(['样例']) })
  await wrapper.findAll('button').find(b => b.text() === '查看详细日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('界面消息')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('原始消息')
  expect(wrapper.get('[role="dialog"]').text()).not.toContain('界面消息')
})

test('shows a compact task name and distinguishes interface and raw log status', async () => {
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: 'MAA', script_name: 'MAA', state: 'succeeded', ui_log_status: '未设置区域', detail_log_status: '已采集' }] }
  const wrapper = setup([record]); await wrapper.setProps({ expanded: new Set(['样例']) })
  const item = wrapper.get('.run-record li')
  expect(item.get('b').text()).toBe('MAA')
  expect(item.text()).toContain('界面日志：未设置区域')
  expect(item.text()).toContain('原始日志：已采集')
  expect(item.get('button').text()).toBe('查看详细日志')
})
