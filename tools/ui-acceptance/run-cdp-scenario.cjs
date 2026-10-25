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
  await evaluate(cdp, `(() => { const host = document.querySelector('.task-tabs'); host.scrollTop = 36; host.querySelectorAll('.task-tab')[2].click() })()`)
  await eventually(cdp, `Boolean(document.querySelector('[aria-label="脚本运行详情"]')) && !document.querySelector('.log-filters') && !document.querySelector('.history-navigator') && !document.querySelector('.task-tabs')`, '脚本详情没有切换到独立页面，或一级日志页面元素仍然可见')
  await eventually(cdp, `document.body.innerText.includes('日志 / 历史运行') && document.body.innerText.includes('本软件事件流')`, '独立详情页面没有呈现面包屑或本软件事件流')
  await evaluate(cdp, click('← 返回运行报告'))
  await eventually(cdp, `document.querySelectorAll('.task-tabs .task-tab').length === 6`, '返回运行报告后没有恢复任务列表')
  await eventually(cdp, `(() => { const host = document.querySelector('.task-tabs'); return host.scrollTop >= 30 && document.activeElement?.dataset.taskId === 'task-3' })()`, '返回运行报告后没有恢复任务列表滚动位置或触发项焦点')
  const reloadUrl = await evaluate(cdp, 'location.href')
  await cdp.call('Page.enable')
  await cdp.call('Page.addScriptToEvaluateOnNewDocument', { source: `(() => { class FixtureWebSocket { static CONNECTING=0; static OPEN=1; readyState=1; constructor(){ globalThis.__fixtureSocket=this; queueMicrotask(() => this.onopen?.()) } send(){} close(){ this.readyState=3 } }; Object.defineProperty(globalThis, 'WebSocket', { value: FixtureWebSocket, configurable: true, writable: true }); globalThis.__fixtureSocketOverrideInstalled = true })();` })
  await cdp.call('Page.navigate', { url: reloadUrl })
  await eventually(cdp, 'globalThis.__fixtureSocketOverrideInstalled === true', '受控 WebSocket 覆盖没有在新页面生效')
  await eventually(cdp, `document.body.innerText.includes('脚本管理')`, '重载后的主界面没有加载')
  await eventually(cdp, `Boolean(window.__fixtureSocket)`, '受控 WebSocket 没有建立')
  const emit = data => evaluate(cdp, `window.__fixtureSocket.onmessage({data: JSON.stringify(${JSON.stringify(data)})})`)
  await emit({ type:'runtime', data:{ busy:true, activeTaskId:'fixture', cooldownSeconds:0, run:{id:'fixture-run',queue_name:'受控运行',state:'running',items:[{task_id:'fixture',task_name:'MAA',state:'waiting_manual',ui_log_status:'已连接受控日志'}]}, runCenter:{currentScriptName:'受控脚本',currentTaskName:'MAA',currentState:'waiting_manual',currentIndex:1,totalPrograms:1}, liveLogs:[{runId:'fixture-run',sequence:1,taskId:'fixture',message:'开始校验任务配置',phase:'validation'},{runId:'fixture-run',sequence:2,taskId:'fixture',message:'等待人工确认当前程序完成',phase:'waiting'}] } })
  await emit({ type:'script_log', data:{runId:'fixture-run',taskId:'fixture',sourceId:'fixture-file',sourceName:'fixture.log',sourceType:'external',sequence:1,append:false,text:'外部来源起始',truncated:false,updatedAt:'2026-10-24T00:00:00Z'} })
  await emit({ type:'script_log', data:{runId:'fixture-run',taskId:'fixture',sourceId:'fixture-window',sourceName:'界面读取',sourceType:'window',sequence:1,append:false,text:'界面来源起始',truncated:false,updatedAt:'2026-10-24T00:00:01Z'} })
  await eventually(cdp, `document.querySelectorAll('.script-source-tabs button').length === 2 && document.body.innerText.includes('等待人工确认当前程序完成')`, '运行中摘要或双来源标签未显示')
  await evaluate(cdp, `[...document.querySelectorAll('.script-source-tabs button')].find(node => node.textContent.includes('fixture.log')).click()`)
  await emit({ type:'script_log', data:{runId:'fixture-run',taskId:'fixture',sourceId:'fixture-window',sourceName:'界面读取',sourceType:'window',sequence:2,append:true,text:'\\n界面来源新增',truncated:false,updatedAt:'2026-10-24T00:00:02Z'} })
  const hasWindowActivity = `[...document.querySelectorAll('.script-source-tabs button')].find(node => node.textContent.includes('界面读取'))?.querySelector('i')`
  try { await eventually(cdp, hasWindowActivity, '未查看来源新增内容后没有活动标记') } catch (error) {
    const tabs = await evaluate(cdp, `JSON.stringify([...document.querySelectorAll('.script-source-tabs button')].map(node => ({ text: node.textContent, active: node.classList.contains('active'), activity: Boolean(node.querySelector('i')) })))`)
    throw new Error(`${error.message}；来源标签状态：${tabs}`)
  }
  await evaluate(cdp, `[...document.querySelectorAll('.script-source-tabs button')].find(node => node.textContent.includes('界面读取')).click()`)
  if (await evaluate(cdp, `[...document.querySelectorAll('.script-source-tabs button')].find(node => node.textContent.includes('界面读取')).querySelector('i') !== null`)) throw new Error('查看来源后活动标记没有清除')
  return 'PASS: 独立脚本详情页、状态恢复、纵向历史任务列表、运行摘要与多来源活动标记'
}

async function main() {
  const endpoint = value('--cdp')
  const scenario = value('--scenario')
  const screenshot = value('--screenshot')
  if (!endpoint) throw new Error('缺少 --cdp。')
  const target = await pageTarget(endpoint)
  const cdp = await connect(target.webSocketDebuggerUrl)
  try {
    if (scenario === 'log') process.stdout.write(`### Result\n${JSON.stringify(await logScenario(cdp))}\n`)
    if (screenshot) {
      const image = await cdp.call('Page.captureScreenshot', { format:'png' })
      fs.writeFileSync(screenshot, Buffer.from(image.data, 'base64'))
    }
  } finally { cdp.close() }
}
main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1 })
