export type DetailEntry = { captured_at: string; text: string }

// 各规则的来源与适用格式见 docs/日志展示规则.md；未知格式始终保留原文。
export function friendlyDetailLine(line: string, previousLine = ''): string {
  const maa = line.match(/^\[([^\]]+)\]\[(?:INF|WRN|ERR|FTL)\]\[Bootstrapper\]\s*<\d+>\s*(.*)$/)
  if (maa) {
    if (maa[2] === 'MaaAssistantArknights GUI started') return `日志时间 ${maa[1]}：MAA 界面已启动`
    const version = maa[2].match(/^Version (v[\w.+-]+)$/)
    if (version) return `日志时间 ${maa[1]}：MAA 版本 ${version[1]}`
  }
  const end = line.match(/^\[([^\]]+)\]\s*任务(开始|完成|失败)[:：]\s*(.+)$/)
  if (end) return `日志时间 ${end[1]}：${end[3]}：${end[2] === '开始' ? '开始执行' : end[2] === '完成' ? '已完成' : '执行失败'}`
  const zzz = line.match(/^\[(\d\d:\d\d:\d\d\.\d+)\]\s*\[[^\]]+\]\s*\[(?:INFO|WARNING|ERROR|DEBUG)\]:\s*指令\[\s*(.+?)\s*\]\s*执行(成功|失败)(.*)$/)
  if (zzz) return `日志时间 ${zzz[1]}：${zzz[2]}：执行${zzz[3]}${zzz[4]}`
  const bgi = previousLine.match(/^\[(\d\d:\d\d:\d\d\.\d+)\]\s*\[INF\].*BetterGenshinImpact\.ViewModel\.Pages\.OneDragonFlowViewModel\s*$/)
  if (bgi && line.trim() === '一条龙和配置组任务结束') return `日志时间 ${bgi[1]}：一条龙和配置组任务已结束`
  const m7 = line.match(/^.*(体力计划已完成[:：]\s*历战余响|历战余响尚未刷新|首次使用请先打开图形界面)\s*$/)
  if (m7) return `软件日志：${m7[1]}`
  return `未识别的日志：${line}`
}

export function friendlyDetailEntries(entries: DetailEntry[]): DetailEntry[] {
  const lines: DetailEntry[] = []
  let pending = '', capturedAt = '', previousLine = ''
  for (const entry of entries) {
    const parts = entry.text.split('\n')
    for (let i = 0; i < parts.length; i++) {
      if (!pending) capturedAt = entry.captured_at
      pending += parts[i]
      if (i < parts.length - 1) {
        if (pending.endsWith('\r')) pending = pending.slice(0, -1)
        if (pending) lines.push({ captured_at: capturedAt, text: friendlyDetailLine(pending, previousLine) })
        previousLine = pending
        pending = ''
      }
    }
  }
  if (pending) lines.push({ captured_at: capturedAt, text: friendlyDetailLine(pending, previousLine) })
  return lines
}
