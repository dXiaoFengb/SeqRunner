const triggers: Record<string, string> = { manual: '手动触发', scheduled: '定时触发', startup: '启动时触发' }
const states: Record<string, string> = { idle: '空闲', running: '运行中', stopping: '停止中', completed: '已完成', stopped: '已停止', queued: '等待运行', validating: '校验中', starting: '启动中', monitoring: '运行中', waiting_manual: '等待人工确认', succeeded: '成功', failed: '失败', closing: '正在关闭', force_closing: '强制关闭中', cooling_down: '冷却中' }
const sources: Record<string, string> = { manual: '人工确认', process_exit: '程序退出', success_log: '成功日志' }
export const triggerLabel = (value: string) => triggers[value] || value
export const stateLabel = (value: string) => states[value] || value
export const sourceLabel = (value: string) => sources[value] || value
export function logLabel(line: string) {
  return line.replace(/^任务成功，来源：CompletionSource\.(MANUAL|PROCESS_EXIT|SUCCESS_LOG)$/, (_, name: string) => `任务成功，来源：${sourceLabel(name.toLowerCase())}`)
}

export function liveLogLabel(line: string, taskName?: string) {
  if (/^已启动受管进程 PID \d+$/.test(line)) return `已启动受管程序 ${taskName || '当前程序'}`
  return logLabel(line)
}
