import { expect, test } from 'vitest'
import { friendlyDetailEntries, friendlyDetailLine } from './detailLogLabels'
import { liveLogLabel } from './logLabels'

test('explains supported formats without asserting overall success', () => {
  expect(friendlyDetailLine('[2026-10-17 21:43:49.493][INF][Bootstrapper] <2> MaaAssistantArknights GUI started')).toContain('MAA 界面已启动')
  expect(friendlyDetailLine('[2026-10-17 21:43:49.494][INF][Bootstrapper] <2> Version v6.20.0-beta.1')).toContain('MAA 版本 v6.20.0-beta.1')
  expect(friendlyDetailLine('[12:00:00.123] [operation.py 11] [INFO]: 指令[ 体力刷本 ] 执行成功')).toContain('体力刷本：执行成功')
  expect(friendlyDetailLine('[2026-10-17 12:00:00.000] 任务失败: 基建')).toContain('基建：执行失败')
  expect(friendlyDetailLine('一条龙和配置组任务结束', '[12:00:00.123] [INF] BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel')).toContain('任务已结束')
  expect(friendlyDetailLine('首次使用请先打开图形界面')).toContain('首次使用请先打开图形界面')
  expect(friendlyDetailLine('User version v1')).toBe('未识别的日志：User version v1')
  expect(liveLogLabel('已启动受管进程 PID 123', 'MAA')).toBe('已启动受管程序 MAA')
})

test('joins partial lines across chunks without changing their order', () => {
  const entries = friendlyDetailEntries([{ captured_at: 'a', text: '[2026-01-01][INF][Bootstrapper] <2> MaaAssistant' }, { captured_at: 'b', text: 'Arknights GUI started\nunknown\n' }])
  expect(entries).toEqual([{ captured_at: 'a', text: '日志时间 2026-01-01：MAA 界面已启动' }, { captured_at: 'b', text: '未识别的日志：unknown' }])
})
