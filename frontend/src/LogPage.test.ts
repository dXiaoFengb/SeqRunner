import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import LogPage from './LogPage.vue'
import type { ReadableReport, RunItem } from './types'

type TestRun = { id: string; queue_name: string; trigger_source: string; state: string; started_at: string; items: RunItem[]; scheduler_skips: string[]; readable_report?: ReadableReport }

const run = (id: string, started_at: string): TestRun => ({ id, queue_name: id, trigger_source: 'manual', state: 'completed', started_at, items: [], scheduler_skips: [] })
const fixture = [run('新年', '2026-01-01T12:00:00'), run('边界', '2025-12-26T12:00:00'), run('旧记录', '2025-12-25T12:00:00')]
const setup = (history: TestRun[] = fixture, selectedId: string | null = null) => mount(LogPage, {
  props: { history, selectedId, expanded: new Set<string>() },
  global: { stubs: { VueMonacoEditor: { props: ['value'], template: '<pre class="monaco-stub">{{ value }}</pre>' } } },
})
const openFirstTaskDetail = async (wrapper: ReturnType<typeof setup>) => { await wrapper.get('.task-tab').trigger('click') }
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
  expect(wrapper.text()).toContain('旧失败'); expect(wrapper.text()).toContain('失败原因'); expect(wrapper.text()).toContain('计划跳过信息')
  await openFirstTaskDetail(wrapper)
  expect(wrapper.text()).toContain('摘要')
})

test('distinguishes no records from no matching records', () => {
  expect(setup([]).text()).toContain('暂无运行记录')
  expect(setup([run('旧', '2020-01-01T12:00:00')]).text()).toContain('没有符合条件的日志')
})

test('shows the run log entry and distinguishes failure', async () => {
  const record = { ...run('失败运行', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'failed', error_summary: '退出失败' }] }
  const wrapper = setup([record])
  expect(wrapper.text()).toContain('存在失败')
  await wrapper.findAll('button').find(button => button.text() === '查看本轮运行日志')!.trigger('click')
  expect(wrapper.text()).toContain('旧记录没有保存完整的本轮运行日志')
})

test('renders a local readable report after opening the selected script detail and keeps legacy fallback visible', async () => {
  const report: ReadableReport = { generator_version: 'local-rules-v1', conclusion: '本轮全部完成', completion_basis: '所有任务均获得完成依据', duration_seconds: 63, risk_count: 1, risks: ['检测到 1 条错误级脚本日志，但未构成终态失败'], events: [] }
  const record: TestRun = { ...run('可读报告', '2026-01-01T12:00:00'), readable_report: report, items: [{ task_id: 't', task_name: '程序', state: 'succeeded', readable_report: { ...report, conclusion: '任务已完成', completion_basis: '人工确认', events: [{ timestamp: '2026-01-01T12:00:00Z', source: 'runner', phase: 'confirmation', severity: 'info', event_type: 'manual.confirmation', message: '等待人工确认', rule_id: 'lifecycle.manual.confirmation' }] } }] }
  const wrapper = setup([record])
  expect(wrapper.get('[aria-label="运行报告"]').text()).toContain('本轮全部完成')
  expect(wrapper.text()).toContain('0 小时 1 分 3 秒')
  expect(wrapper.text()).toContain('检测到 1 条错误级脚本日志，但未构成终态失败')
  await openFirstTaskDetail(wrapper)
  expect(wrapper.text()).toContain('等待人工确认')
  expect(wrapper.text()).toContain('根据已确认运行事实生成，不是脚本原文')
  expect(wrapper.find('.history-workspace').exists()).toBe(true)
})

test('renders a selected script as a standalone detail page and asks the app shell to restore the report', async () => {
  const report: ReadableReport = { generator_version: 'local-rules-v1', conclusion: '任务已完成', completion_basis: '人工确认', duration_seconds: 4, risk_count: 0, risks: [], events: [] }
  const record: TestRun = { ...run('独立页面运行', '2026-01-01T12:00:00'), items: [{ task_id: 'detail', task_name: '独立脚本', state: 'succeeded', readable_report: report, external_log_files: ['C:/受控/runner.log'] }] }
  const wrapper = mount(LogPage, {
    props: { history: [record], selectedId: '独立页面运行', expanded: new Set<string>(), standalone: true, detailRunId: '独立页面运行', detailTaskId: 'detail' },
    global: { stubs: { VueMonacoEditor: { props: ['value'], template: '<pre class="monaco-stub">{{ value }}</pre>' } } },
  })
  await wrapper.vm.$nextTick()
  expect(wrapper.get('[aria-label="脚本运行详情"]').text()).toContain('日志 / 历史运行 / 独立脚本')
  expect(wrapper.find('.log-filters').exists()).toBe(false)
  expect(wrapper.find('.history-navigator').exists()).toBe(false)
  expect(wrapper.find('.task-tabs').exists()).toBe(false)
  expect(wrapper.get('[aria-label="脚本运行详情"]').text()).toContain('查看脚本内部日志')
  await wrapper.get('button.text-button').trigger('click')
  expect(wrapper.emitted('returnDetail')).toHaveLength(1)
})

test('filters records by keyword and trigger source together, resets defaults, and keeps script navigation explicit', async () => {
  const report: ReadableReport = { generator_version: 'local-rules-v1', conclusion: '已完成', completion_basis: '成功规则', duration_seconds: 1, risk_count: 0, risks: [], events: [] }
  const records = [
    { ...run('定时命中', '2026-01-01T12:00:00'), trigger_source: 'scheduled', readable_report: report, items: [{ task_id: 'a', task_name: '甲', state: 'succeeded', error_summary: '网络重试', readable_report: report }] },
    { ...run('手动命中', '2026-01-01T11:00:00'), trigger_source: 'manual', items: [{ task_id: 'b', task_name: '乙', state: 'succeeded', error_summary: '网络重试' }] },
  ]
  const wrapper = setup(records)
  await wrapper.get('[aria-label="关键词"]').setValue('网络重试')
  await wrapper.get('[aria-label="触发来源"]').setValue('scheduled')
  await wrapper.findAll('.filter-actions button')[0].trigger('click')
  expect(wrapper.text()).toContain('定时命中'); expect(wrapper.text()).not.toContain('手动命中')
  const first = wrapper.get('.task-tab')
  await first.trigger('mouseenter')
  expect(wrapper.find('[aria-label="脚本详情"]').exists()).toBe(false)
  await first.trigger('click')
  expect(wrapper.get('[aria-label="脚本详情"]').text()).toContain('甲')
  await wrapper.get('button.text-button').trigger('click')
  expect(wrapper.find('.task-tab').exists()).toBe(true)
  expect(wrapper.get('.task-tab').classes()).toContain('selected')
  await wrapper.findAll('.filter-actions button')[1].trigger('click')
  expect((wrapper.get('[aria-label="关键词"]').element as HTMLInputElement).value).toBe('')
  expect((wrapper.get('[aria-label="触发来源"]').element as HTMLSelectElement).value).toBe('all')
})

test('shows unrecognized external record count only in the selected script detail', async () => {
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', unrecognized_log_count: 4 }] }
  const wrapper = setup([record])
  expect(wrapper.text()).not.toContain('未识别的外部原始记录')
  await openFirstTaskDetail(wrapper)
  expect(wrapper.text()).toContain('未识别的外部原始记录：4 条（仅统计，不解释原文）')
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
  expect(wrapper.text()).toContain('定时触发 · 已完成'); expect(wrapper.text()).toContain('成功：人工确认')
  await openFirstTaskDetail(wrapper)
  expect(wrapper.text()).toContain('任务成功，来源：人工确认'); expect(wrapper.text()).toContain('外部 manual')
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
  await openFirstTaskDetail(wrapper)
  await wrapper.findAll('button').find(b => b.text() === '查看界面采集日志')!.trigger('click')
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
  await openFirstTaskDetail(wrapper)
  await wrapper.findAll('button').find(b => b.text() === '查看界面采集日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('暂无法可靠获取该脚本的界面日志')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('未配置日志路径')
  expect(request).not.toHaveBeenCalled()
})

test('shows captured interface logs separately from raw logs', async () => {
  const request = vi.fn(async (path: string) => ({ entries: [{ captured_at: '2026-01-01T01:00:00Z', text: path.includes('/ui?') ? '界面消息' : '原始消息' }], next_offset: null }))
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', detail_log_id: 'raw', ui_log_id: 'ui', ui_log_status: '已采集界面日志' }] }
  const wrapper = setup([record]); await wrapper.setProps({ request, expanded: new Set(['样例']) })
  await openFirstTaskDetail(wrapper)
  await wrapper.findAll('button').find(b => b.text() === '查看界面采集日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('界面消息')
  await wrapper.findAll('button').find(b => b.text() === '查看原始日志')!.trigger('click')
  expect(wrapper.get('[role="dialog"]').text()).toContain('原始消息')
  expect(wrapper.get('[role="dialog"]').text()).not.toContain('界面消息')
})

test('shows a compact task tab then distinguishes interface and raw log status in its detail', async () => {
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: 'MAA', script_name: 'MAA', state: 'succeeded', ui_log_status: '未设置区域', detail_log_status: '已采集' }] }
  const wrapper = setup([record]); await wrapper.setProps({ expanded: new Set(['样例']) })
  const item = wrapper.get('.task-tab')
  expect(item.get('b').text()).toBe('MAA')
  expect(wrapper.text()).not.toContain('查看脚本内部日志')
  await openFirstTaskDetail(wrapper)
  expect(wrapper.text()).toContain('界面采集：未设置区域')
  expect(wrapper.text()).toContain('旧本地详细日志：已采集')
  expect(wrapper.get('[aria-label="脚本详情"]').text()).toContain('查看界面采集日志')
})

test('stacks task choices vertically while keeping each whole choice keyboard-accessible', async () => {
  const record = { ...run('纵向运行', '2026-01-01T12:00:00'), items: [
    { task_id: 'first', task_name: '第一项', state: 'succeeded' },
    { task_id: 'second', task_name: '第二项', state: 'failed', error_summary: '受控失败' },
    { task_id: 'third', task_name: '第三项', state: 'stopped' },
  ] }
  const wrapper = setup([record]); await wrapper.setProps({ expanded: new Set(['纵向运行']) })
  const choices = wrapper.findAll('.task-tabs > .task-tab')
  expect(choices).toHaveLength(3)
  expect(choices.every((choice) => choice.element.tagName === 'BUTTON')).toBe(true)
  expect(choices[1].get('.task-tab-main').text()).toContain('第二项')
  expect(choices[1].get('.task-tab-meta').text()).toContain('查看详情')
  await choices[1].trigger('click')
  expect(wrapper.get('[aria-label="脚本详情"]').text()).toContain('第二项')
  await wrapper.get('button.text-button').trigger('click')
  expect(wrapper.findAll('.task-tabs > .task-tab')).toHaveLength(3)
  expect(wrapper.findAll('.task-tab.selected')).toHaveLength(1)
})

test('lists and pages only external log files recorded by the history item', async () => {
  const exportFile = vi.fn(async () => ({ canceled: false }))
  const showInFolder = vi.fn(async () => ({ accepted: true }))
  window.desktopRunner = { getConnection: async () => null, selectExecutable: async () => ({ canceled: true }), exportFile, showInFolder }
  const request = vi.fn(async (path: string) => {
    if (!path.includes('/0?')) return { files: [{ index: 0, path: 'C:/受控/part.log', name: 'part.log', available: true }], status: '已记录 1 个外部日志文件' }
    return path.endsWith('offset=0') ? { text: '第一段', next_offset: 3 } : { text: '第二段', next_offset: null }
  })
  const record = { ...run('样例', '2026-01-01T12:00:00'), items: [{ task_id: 't', task_name: '程序', state: 'succeeded', external_log_files: ['C:/受控/part.log'], external_log_status: '已记录 1 个外部日志文件' }] }
  const wrapper = setup([record]); await wrapper.setProps({ request, expanded: new Set(['样例']) })
  await openFirstTaskDetail(wrapper)
  await wrapper.findAll('button').find(button => button.text() === '查看脚本内部日志')!.trigger('click')
  expect(wrapper.get('[aria-label="脚本内部日志"]').text()).toContain('第一段')
  await wrapper.findAll('button').find(button => button.text() === '加载更多')!.trigger('click')
  expect(wrapper.get('[aria-label="脚本内部日志"]').text()).toContain('第一段第二段')
  expect(wrapper.get('.external-header-actions').text()).toContain('part.log')
  expect(wrapper.get('.external-actions').classes()).toContain('detail-buttons')
  expect(request.mock.calls.every(([path]) => path.includes('/external-logs/'))).toBe(true)
  await wrapper.findAll('button').find(button => button.text() === '导出原文件')!.trigger('click')
  await wrapper.findAll('button').find(button => button.text() === '打开所在文件夹')!.trigger('click')
  expect(exportFile).toHaveBeenCalledWith({ runId: '样例', taskId: 't', index: 0 })
  expect(showInFolder).toHaveBeenCalledWith({ runId: '样例', taskId: 't', index: 0 })
  delete window.desktopRunner
})
