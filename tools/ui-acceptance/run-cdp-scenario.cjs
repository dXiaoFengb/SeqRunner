const fs = require('node:fs')

const value = name => {
  const index = process.argv.indexOf(name)
  if (index >= 0) return process.argv[index + 1]
  const prefix = `${name}=`
  return process.argv.find(argument => argument.startsWith(prefix))?.slice(prefix.length)
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))

async function pageTarget(endpoint) {
  for (let attempt = 0; attempt < 180; attempt += 1) {
    const targets = await (await fetch(`${endpoint}/json/list`)).json()
    const target = targets.find(item => item.type === 'page' && item.webSocketDebuggerUrl)
    if (target) return target
    await delay(250)
  }
  throw new Error('Electron 调试端口在 45 秒内没有出现渲染页面。')
}

async function browserTarget(endpoint) {
  const metadata = await (await fetch(`${endpoint}/json/version`)).json()
  if (!metadata.webSocketDebuggerUrl) throw new Error('Electron 调试端口没有提供浏览器级 CDP 通道。')
  return metadata
}

async function connect(url) {
  const socket = new WebSocket(url)
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject })
  let nextId = 1
  const pending = new Map()
  socket.onmessage = async event => {
    const raw = typeof event.data === 'string' ? event.data : await event.data.text()
    const message = JSON.parse(raw)
    const request = pending.get(message.id)
    if (!request) return
    pending.delete(message.id)
    message.error ? request.reject(new Error(message.error.message)) : request.resolve(message.result)
  }
  return {
    call(method, params = {}) {
      const id = nextId++
      socket.send(JSON.stringify({ id, method, params }))
      return new Promise((resolve, reject) => pending.set(id, { resolve, reject }))
    },
    notify(method, params = {}) {
      socket.send(JSON.stringify({ id: nextId++, method, params }))
    },
    close() { socket.close() },
  }
}

async function evaluate(cdp, expression) {
  const result = await cdp.call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text || '页面表达式失败')
  return result.result.value
}

async function eventually(cdp, expression, message) {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (await evaluate(cdp, expression)) return
    await delay(150)
  }
  throw new Error(message)
}

async function logScenario(cdp) {
  const click = label => `(() => { const node = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === ${JSON.stringify(label)}); if (!node) throw new Error('找不到按钮：${label}'); node.click() })()`
  await eventually(cdp, `document.body.innerText.includes('脚本管理')`, '主界面没有加载')
  await evaluate(cdp, click('日志'))
  await eventually(cdp, `document.querySelector('.history-navigator .run-record')`, '历史导航没有加载')
  await evaluate(cdp, click('最近三个月'))
  await evaluate(cdp, click('搜索'))
  await eventually(cdp, `[...document.querySelectorAll('.history-navigator .run-record')].some(node => node.textContent.includes('旧验收记录'))`, '受控旧历史记录未出现')
  await evaluate(cdp, `[...document.querySelectorAll('.history-navigator .run-record')].find(node => node.textContent.includes('旧验收记录')).click()`)
  await eventually(cdp, `document.querySelectorAll('.task-tabs .task-tab').length === 6`, '纵向任务列表没有呈现六项受控任务')
  const vertical = await evaluate(cdp, `(() => { const host = document.querySelector('.task-tabs'); const rows = [...host.querySelectorAll('.task-tab')].map(node => node.getBoundingClientRect()); return { count: rows.length, vertical: rows.every((row, index) => index === 0 || row.top >= rows[index - 1].bottom), internalScroll: host.scrollHeight > host.clientHeight && getComputedStyle(host).overflowX === 'hidden' && host.scrollWidth <= host.clientWidth + 1 } })()`)
  if (!vertical.vertical || !vertical.internalScroll) throw new Error('历史任务列表没有保持纵向内部滚动布局')
  await evaluate(cdp, `(() => { const host = document.querySelector('.task-tabs'); const task = host.querySelectorAll('.task-tab')[0]; host.scrollTop = 36; task.dispatchEvent(new PointerEvent('pointerdown', {bubbles:true})); task.focus(); task.click() })()`)
  await eventually(cdp, `Boolean(document.querySelector('[aria-label="脚本运行详情"]')) && !document.querySelector('.log-filters') && !document.querySelector('.history-navigator') && !document.querySelector('.task-tabs')`, '脚本详情没有切换到独立页面，或一级日志页面元素仍然可见')
  await eventually(cdp, `document.body.innerText.includes('日志 / 历史运行') && document.body.innerText.includes('脚本可读事件流')`, '独立详情页面没有呈现面包屑或脚本转译事件流')
  const reader = await evaluate(cdp, `(() => { const host = document.querySelector('.derived-event-editor'); return host ? { height: host.clientHeight, overflow: getComputedStyle(host).overflow, scrollHeight: host.scrollHeight } : null })()`)
  if (!reader || reader.height < 400 || reader.height > 460 || reader.overflow !== 'hidden') throw new Error(`脚本可读事件流没有使用固定的内部阅读窗口：${JSON.stringify(reader)}`)
  await evaluate(cdp, click('查看脚本内部日志'))
  await eventually(cdp, `Boolean(document.querySelector('[aria-label="脚本内部日志"]')) && document.body.innerText.includes('part-01.log') && document.body.innerText.includes('missing.log（不可读取）')`, '受限脚本原文阅读器或多文件引用没有打开')
  await eventually(cdp, `document.querySelector('[aria-label="脚本内部日志"]')?.innerText.includes('外部日志首段')`, '外部原文首页没有读取已授权的第一个文件')
  await evaluate(cdp, click('加载更多'))
  await eventually(cdp, `(() => { const dialog = document.querySelector('[aria-label="脚本内部日志"]'); return Boolean(dialog) && ![...dialog.querySelectorAll('button')].some(node => node.textContent.trim() === '加载更多') && dialog.innerText.includes('已读取完整文件') && !dialog.querySelector('[role="alert"]') })()`, '外部原文分页没有完成或出现读取错误')
  await evaluate(cdp, `(() => { const button = [...document.querySelectorAll('.external-files button')].find(node => node.textContent.includes('part-02.log')); if (!button) throw new Error('找不到第二个外部日志文件'); button.click() })()`)
  await eventually(cdp, `document.querySelector('[aria-label="脚本内部日志"]')?.innerText.includes('第二个外部日志')`, '切换第二个受控外部日志文件失败')
  if (!await evaluate(cdp, `[...document.querySelectorAll('.external-files button')].some(node => node.textContent.includes('missing.log') && node.disabled)`)) throw new Error('不可读的历史外部日志没有明确标记为不可读取')
  await evaluate(cdp, click('关闭'))
  await eventually(cdp, `!document.querySelector('[aria-label="脚本内部日志"]')`, '脚本原文阅读器没有关闭')
  await evaluate(cdp, click('← 返回运行报告'))
  await eventually(cdp, `document.querySelectorAll('.task-tabs .task-tab').length === 6`, '返回运行报告后没有恢复任务列表')
  for (let attempt = 0; attempt < 100; attempt += 1) {
    const restored = await evaluate(cdp, `(() => { const host = document.querySelector('.task-tabs'); return { scrollTop: host?.scrollTop ?? null, scrollHeight: host?.scrollHeight ?? null, clientHeight: host?.clientHeight ?? null, activeTaskId: document.activeElement?.dataset.taskId || null, selectedTaskId: host?.querySelector('.task-tab.selected')?.dataset.taskId || null, taskIds: host ? [...host.querySelectorAll('.task-tab')].map(node => node.dataset.taskId) : [], restoreTaskId: host?.closest('[data-restore-task-id]')?.dataset.restoreTaskId || null } })()`)
    if (restored.scrollTop >= 30 && restored.activeTaskId === 'task') break
    if (attempt === 99) throw new Error(`返回运行报告后没有恢复任务列表滚动位置或触发项焦点：${JSON.stringify(restored)}`)
    await delay(150)
  }
  const reloadUrl = await evaluate(cdp, 'location.href')
  await cdp.call('Page.enable')
  await cdp.call('Page.addScriptToEvaluateOnNewDocument', { source: `(() => { const nativeFetch = globalThis.fetch.bind(globalThis); globalThis.fetch = async (input, init) => { const url = typeof input === 'string' ? input : input?.url || ''; if (/\\/api\\/runtime(?:\\?|$)/.test(url) && globalThis.__fixtureRuntime) return new Response(JSON.stringify(globalThis.__fixtureRuntime), { status: 200, headers: { 'content-type': 'application/json' } }); return nativeFetch(input, init) }; class FixtureWebSocket { static CONNECTING=0; static OPEN=1; readyState=1; constructor(){ globalThis.__fixtureSocket=this; queueMicrotask(() => this.onopen?.()) } send(){} close(){ this.readyState=3 } }; Object.defineProperty(globalThis, 'WebSocket', { value: FixtureWebSocket, configurable: true, writable: true }); globalThis.__fixtureSocketOverrideInstalled = true })();` })
  await cdp.call('Page.navigate', { url: reloadUrl })
  await eventually(cdp, 'globalThis.__fixtureSocketOverrideInstalled === true', '受控 WebSocket 覆盖没有在新页面生效')
  await eventually(cdp, `document.body.innerText.includes('脚本管理')`, '重载后的主界面没有加载')
  await eventually(cdp, `Boolean(window.__fixtureSocket)`, '受控 WebSocket 没有建立')
  const emit = data => evaluate(cdp, `window.__fixtureSocket.onmessage({data: JSON.stringify(${JSON.stringify(data)})})`)
  const scriptEvent = (sequence, message, severity, count = 1) => ({ runId:'fixture-run', taskId:'fixture', sequence, truncated:false, replace: count > 1, event:{ timestamp:'2026-10-25T00:00:00Z', last_timestamp: count > 1 ? '2026-10-25T00:00:03Z' : undefined, occurrence_count:count, task_id:'fixture', task_name:'MAA', source:'script', sequence:1, phase:'脚本日志', severity, event_type:'script.log.translated', message, rule_id: severity === 'warning' ? 'script.level.warning' : 'script.module.configurationhelper' } })
  const initialEvent = scriptEvent(1, '脚本正在更新配置。', 'info', 3)
  const runtime = { busy:true, activeTaskId:'fixture', cooldownSeconds:0, run:{id:'fixture-run',queue_name:'受控运行',state:'running',items:[{task_id:'fixture',task_name:'MAA',state:'waiting_manual',log_source_configured:true,script_translation_version:'script-local-rules-v1'}]}, runCenter:{currentScriptName:'受控脚本',currentTaskName:'MAA',currentState:'waiting_manual',currentIndex:1,totalPrograms:1}, liveLogs:[{runId:'fixture-run',sequence:1,taskId:'fixture',message:'开始校验任务配置',phase:'validation'},{runId:'fixture-run',sequence:2,taskId:'fixture',message:'等待人工确认当前程序完成',phase:'waiting'}], scriptEvents:[initialEvent] }
  await evaluate(cdp, `globalThis.__fixtureRuntime = ${JSON.stringify(runtime)}`)
  await emit({ type:'runtime', data:runtime })
  const foldedEvent = scriptEvent(1, '脚本正在更新配置。', 'info', 4)
  runtime.scriptEvents = [foldedEvent]
  await emit({ type:'script_event', data:foldedEvent })
  await eventually(cdp, `document.querySelector('.script-live-list')?.innerText.includes('脚本正在更新配置。') && document.querySelector('.script-live-list')?.innerText.includes('×4')`, '连续折叠脚本事件没有以原序号更新')
  const warningEvent = scriptEvent(3, '脚本记录了警告级信息；请查看脚本内部日志确认原文。', 'warning')
  runtime.scriptEvents = [foldedEvent, warningEvent]
  await evaluate(cdp, `globalThis.__fixtureRuntime = ${JSON.stringify(runtime)}`)
  await emit({ type:'script_event', data:warningEvent })
  await eventually(cdp, `document.querySelector('.script-live-list')?.innerText.includes('脚本记录了警告级信息') && document.querySelector('.scheduler-live-list')?.innerText.includes('等待人工确认当前程序完成')`, '运行中摘要、脚本实时转译或序号断档重同步未显示')
  if (await evaluate(cdp, `document.querySelector('.script-live-list')?.innerText.includes('等待人工确认当前程序完成')`)) throw new Error('本软件运行状态被错误混入脚本实时转译')
  if (await evaluate(cdp, `document.querySelector('.scheduler-live-list')?.innerText.includes('脚本记录了警告级信息')`)) throw new Error('脚本实时转译被错误混入本软件运行状态')
  if (await evaluate(cdp, `document.querySelectorAll('.script-source-tabs').length !== 0`)) throw new Error('脚本实时转译仍显示来源选择条')
  return 'PASS: 唯一 CDP 日志链路覆盖历史筛选、纵向详情、受限原文分页、多文件不可读状态、折叠更新、快照重同步、状态分层与无来源选择条'
}

async function settingsScenario(cdp, screenshotDirectory, windowWidth) {
  const clickTab = label => `(() => { const node = [...document.querySelectorAll('[role="tab"]')].find(tab => tab.textContent.trim() === ${JSON.stringify(label)}); if (!node) throw new Error('找不到设置页签：${label}'); node.click() })()`
  await eventually(cdp, `document.body.innerText.includes('脚本管理')`, '主界面没有加载')
  await evaluate(cdp, `(() => { const node = [...document.querySelectorAll('nav button')].find(button => button.textContent.includes('设置')); if (!node) throw new Error('找不到设置导航'); node.click() })()`)
  await eventually(cdp, `document.querySelector('[role="tablist"]') && document.querySelectorAll('[role="tab"]').length === 4`, '设置中心的四个页签没有加载')
  const labels = await evaluate(cdp, `[...document.querySelectorAll('[role="tab"]')].map(tab => tab.textContent.trim())`)
  const expected = ['功能设置', '通知设置', '日志管理', '关于']
  if (JSON.stringify(labels) !== JSON.stringify(expected)) throw new Error(`设置页签顺序不符合预期：${JSON.stringify(labels)}`)
  if (await evaluate(cdp, `document.body.innerText.includes('本地设置') || document.body.innerText.includes('界面设置') || document.body.innerText.includes('开发者工具')`)) throw new Error('设置中心仍保留已移除的设置入口')
  await evaluate(cdp, `document.getElementById('settings-tab-function').focus()`)
  if (!await evaluate(cdp, `document.activeElement?.id === 'settings-tab-function'`)) throw new Error('设置页签无法通过键盘焦点访问')
  if (!await evaluate(cdp, `(() => { const tab = document.getElementById('settings-tab-function'); return tab instanceof HTMLButtonElement && !tab.disabled && tab.tabIndex >= 0 })()`)) throw new Error('设置页签不是可由键盘激活的原生按钮')
  await evaluate(cdp, `document.getElementById('settings-tab-function').click()`)
  await eventually(cdp, `document.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === '功能设置'`, '设置页签无法切换')
  await eventually(cdp, `Boolean(document.querySelector('[aria-label="开机自启"]')) && Boolean(document.querySelector('[aria-label="启动后直接最小化"]')) && document.body.innerText.includes('后端运行方式') && document.body.innerText.includes('由应用 Runtime 监督')`, '功能设置没有同时呈现启动配置与受管运行方式')
  const functionGrouping = await evaluate(cdp, `(() => { const blocks = [...document.querySelectorAll('[data-testid="settings-function-block"]')]; return { blockCount: blocks.length, labels: blocks.map(block => block.querySelector('h3')?.textContent?.trim()), cards: blocks.every(block => block.classList.contains('settings-card')), separate: blocks.length === 2 && blocks[0].parentElement === blocks[1].parentElement && blocks[0].getBoundingClientRect().bottom <= blocks[1].getBoundingClientRect().top, select: document.querySelector('[aria-label="开机自启"]')?.tagName === 'SELECT' } })()`)
  if (functionGrouping.blockCount !== 2 || JSON.stringify(functionGrouping.labels) !== JSON.stringify(['启动配置', '后端运行方式']) || !functionGrouping.cards || !functionGrouping.separate || !functionGrouping.select) throw new Error(`功能设置没有以两个独立板块和选择框呈现：${JSON.stringify(functionGrouping)}`)
  await evaluate(cdp, clickTab('通知设置'))
  await eventually(cdp, `Boolean(document.querySelector('[aria-label="推送任务结果时机"]')) && document.body.innerText.includes('QQ 官方机器人')`, '通知条件或 QQ 渠道没有呈现')
  if (!await evaluate(cdp, `document.querySelector('[aria-label="推送任务结果时机"]')?.tagName === 'SELECT' && document.body.innerText.includes('系统通知') && document.body.innerText.includes('自定义 Webhook') && document.body.innerText.includes('添加 Webhook')`)) throw new Error('通知条件选择框或通知渠道入口未呈现')
  const notificationCardCount = await evaluate(cdp, `document.querySelectorAll('.settings-card').length`)
  await evaluate(cdp, clickTab('日志管理'))
  await eventually(cdp, `document.body.innerText.includes('导出数据备份 ZIP')`, '日志管理入口没有呈现')
  const logCardCount = await evaluate(cdp, `document.querySelectorAll('.settings-card').length`)
  await evaluate(cdp, clickTab('关于'))
  await eventually(cdp, `document.body.innerText.includes('应用信息') && document.body.innerText.includes('连接诊断')`, '关于页诊断信息没有呈现')
  const aboutCardCount = await evaluate(cdp, `document.querySelectorAll('.settings-card').length`)
  const cardCounts = { notifications: notificationCardCount, logs: logCardCount, about: aboutCardCount }
  if (cardCounts.notifications < 1 || cardCounts.logs < 3 || cardCounts.about < 2) throw new Error(`设置页签没有使用统一深蓝分组卡：${JSON.stringify(cardCounts)}`)
  await evaluate(cdp, clickTab('日志管理'))
  await eventually(cdp, `document.body.innerText.includes('导出数据备份 ZIP') && document.body.innerText.includes('导出本体日志 ZIP') && document.body.innerText.includes('问题包')`, '日志管理的受限导出入口没有呈现')
  await evaluate(cdp, clickTab('关于'))
  await eventually(cdp, `document.body.innerText.includes('应用信息') && document.body.innerText.includes('连接诊断')`, '关于页诊断信息没有呈现')
  if (screenshotDirectory) {
    if (![640, 960, 1280].includes(windowWidth)) throw new Error('settings 场景缺少合法的原生窗口宽度。')
    fs.mkdirSync(screenshotDirectory, { recursive: true })
    await cdp.call('Page.enable')
    const tabByWidth = { 1280: '功能设置', 960: '日志管理', 640: '关于' }
    await evaluate(cdp, clickTab(tabByWidth[windowWidth]))
    await eventually(cdp, `outerWidth === ${windowWidth}`, `原生窗口未以 ${windowWidth}px 宽度创建`)
    const layout = await evaluate(cdp, `(() => ({ windowWidth: outerWidth, viewport: innerWidth, documentWidth: document.documentElement.scrollWidth, workbenchWidth: Math.ceil(document.querySelector('.settings-workbench')?.getBoundingClientRect().width || 0), tabWidth: Math.ceil(document.querySelector('.settings-tabs')?.scrollWidth || 0), tabClientWidth: Math.ceil(document.querySelector('.settings-tabs')?.clientWidth || 0) }))()`)
    if (layout.documentWidth > layout.viewport + 1) throw new Error(`设置页在 ${windowWidth}px 出现页面级横向溢出：${JSON.stringify(layout)}`)
    const image = await cdp.call('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false })
    fs.writeFileSync(`${screenshotDirectory}/settings-${windowWidth}.png`, Buffer.from(image.data, 'base64'))
  }
  return `PASS: 设置中心四页签、键盘访问、启动配置与受管运行方式、通知入口、日志管理、关于诊断及 ${windowWidth}px 真实窗口布局均已在隔离 Electron 渲染页核验`
}

async function main() {
  const endpoint = value('--cdp')
  const scenario = value('--scenario')
  const screenshot = value('--screenshot')
  const close = process.argv.includes('--close')
  if (!endpoint) throw new Error('缺少 --cdp。')
  if (close) {
    const browser = await browserTarget(endpoint)
    const cdp = await connect(browser.webSocketDebuggerUrl)
    try {
      // Chromium intentionally closes this websocket before replying to
      // Browser.close. Delivery is then proven by the tracked process tree,
      // not by waiting for an impossible CDP response.
      cdp.notify('Browser.close')
      process.stdout.write('### Close\nrequested\n')
      return
    } finally { cdp.close() }
  }
  const target = await pageTarget(endpoint)
  const cdp = await connect(target.webSocketDebuggerUrl)
  try {
    if (scenario === 'log') process.stdout.write(`### Result\n${JSON.stringify(await logScenario(cdp))}\n`)
    if (scenario === 'settings') process.stdout.write(`### Result\n${JSON.stringify(await settingsScenario(cdp, value('--settings-screenshots'), Number(value('--window-width'))))}\n`)
    if (screenshot) {
      const image = await cdp.call('Page.captureScreenshot', { format:'png' })
      fs.writeFileSync(screenshot, Buffer.from(image.data, 'base64'))
    }
  } finally { cdp.close() }
}
main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1 })
