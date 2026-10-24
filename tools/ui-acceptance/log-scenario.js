async page => {
  const check = (ok, message) => { if (!ok) throw new Error(message) }
  const record = name => page.locator('.history-navigator .run-record').filter({hasText:name})
  // Electron 在持续刷新状态时会使 Playwright 的“稳定”探测长期不收敛；定位、可见性和每次操作后的结果断言仍由本场景覆盖。
  const click = locator => locator.click({force:true})
  // 界面请求使用受控响应；真实框选及 OCR 由独立的 Windows 夹具验证。
  const region = { executable:'C:\\fixture\\MAA.exe', window_class:'FixtureWindow', client_width:640, client_height:480, x:20, y:30, width:200, height:100 }
  await page.route('**/api/ui-log/**', async route => {
    const path = new URL(route.request().url()).pathname
    const data = path.endsWith('/windows') ? [{handle:1,pid:10,created_at:1,title:'受控窗口',window_class:'FixtureWindow',client_width:640,client_height:480}] : path.endsWith('/select') ? {canceled:false,region} : path.endsWith('/frame') ? {image:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR9sAAAAASUVORK5CYII='} : {text:'受控预览日志'}
    await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(data)})
  })
  // 应用默认停留在脚本页；不重复点击已激活的导航项，避免把持续刷新的日志布局误判为导航失败。
  await page.getByRole('heading',{name:'脚本管理',exact:true}).waitFor()
  await click(page.getByRole('button',{name:'编辑脚本'}).first())
  await click(page.getByRole('button',{name:'查找已打开的窗口'}))
  await page.locator('button:not(:disabled)', {hasText:'框选并预览'}).waitFor({timeout:15000})
  await click(page.getByRole('button',{name:'框选并预览'}))
  await page.getByText('受控预览日志').waitFor()
  // 验收器按规则隐藏启动，组件会在 document.hidden 时暂停屏幕画面抓取；可见窗口的画面框选由人工验收第 15、16 项核对。
  await click(page.getByRole('button',{name:'确认并保存区域'}))
  await page.getByText('已设置日志区域',{exact:false}).waitFor()
  await click(page.getByRole('button',{name:'保存更改'}))
  await click(page.getByRole('button',{name:'← 返回',exact:true}))
  await click(page.getByRole('button',{name:'编辑脚本'}).first())
  check(await page.getByText('已设置日志区域',{exact:false}).isVisible(), '重新打开后日志区域未保存')
  await click(page.getByRole('button',{name:'← 返回',exact:true}))
  await click(page.getByRole('button',{name:'新建脚本'}))
  await page.getByLabel('软件预设').selectOption('maa')
  await page.getByPlaceholder('请选择含 MAA.exe 的目录').waitFor({timeout:15000})
  await click(page.getByRole('button',{name:'保存更改',exact:true}))
  await page.locator('.editor-sheet-actions .save-saved').waitFor({timeout:15000})
  await click(page.getByRole('button',{name:'← 返回',exact:true}))
  await click(page.getByRole('button',{name:'编辑脚本'}).last())
  check(await page.getByPlaceholder('请选择含 MAA.exe 的目录').isVisible(), '新脚本 MAA 预设没有保存')
  await click(page.getByRole('button', { name: '日志', exact: true }))
  await record('今日验收记录').waitFor({timeout:15000})
  check(await page.getByText('验收计划跳过').isVisible(), '计划跳过信息缺失')
  check(await record('旧验收记录').count() === 0, '默认范围显示旧记录')
  await click(page.getByRole('button', { name: '最近三个月' }))
  await click(page.getByRole('button', { name: '搜索', exact: true }))
  check(await record('旧验收记录').isVisible(), '日期筛选未显示旧记录')
  const oldRecord = record('旧验收记录')
  check((await oldRecord.textContent()).includes('存在失败'), '运行结果未区分失败')
  await click(oldRecord)
  check(await page.getByLabel('运行报告').getByText('本轮结束，存在失败或未执行项').isVisible(), '可读运行报告未显示最终结论')
  const taskTabs = page.locator('.task-tabs')
  check(await page.locator('.task-tab').count() === 6, '受控多任务运行没有完整呈现为任务选择列表')
  const taskRows = await page.locator('.task-tab').evaluateAll(nodes => nodes.map(node => {
    const box = node.getBoundingClientRect()
    return { top: box.top, bottom: box.bottom, left: box.left, right: box.right }
  }))
  check(taskRows.every((row, index) => index === 0 || row.top >= taskRows[index - 1].bottom), '任务选择项没有从上至下纵向排列')
  check(await taskTabs.evaluate(node => {
    const style = getComputedStyle(node)
    return style.overflowX === 'hidden' && node.scrollWidth <= node.clientWidth + 1 && node.scrollHeight > node.clientHeight
  }), '任务列表没有限制在自身纵向滚动区域内')
  check(await page.getByRole('button', {name:'查看 验收任务 的脚本详情'}).isVisible(), '脚本选项卡没有可访问详情入口')
  await click(page.getByRole('button', {name:'查看 验收任务 的脚本详情'}))
  check(await page.getByLabel('脚本详情').isVisible(), '未进入脚本详情页面')
  check(await page.getByText('根据已确认运行事实生成，不是脚本原文').isVisible(), '可读事件流缺少来源说明')
  check(await page.locator('.derived-event-editor .monaco-editor').count() === 1, '可读事件流没有以只读代码式阅读器呈现')
  await click(page.getByRole('button',{name:'查看本轮运行日志'}))
  await page.getByText('受控本轮日志：总耗时 12 秒').waitFor({timeout:15000})
  await click(page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}))
  await page.getByLabel('合并模式').selectOption('week')
  check((await page.locator('.log-group h2').allTextContents()).every(text => /\d{4}-W\d{2}/.test(text)), '按周分组错误')
  await page.getByLabel('合并模式').selectOption('month')
  check((await page.locator('.log-group h2').allTextContents()).every(text => /\d{4}-\d{2}/.test(text)), '按月分组错误')
  await page.getByLabel('合并模式').selectOption('day')
  await click(record('旧验收记录'))
  await click(page.getByRole('button', {name:'查看 验收任务 的脚本详情'}))
  check(await page.getByLabel('运行报告').getByText('受控失败', {exact:false}).count() >= 1, '失败详情不可见')
  check(await page.getByLabel('运行报告').getByText('受控摘要', {exact:false}).count() >= 1, '日志摘要不可见')
  check(await page.getByText('根据已确认运行事实生成，不是脚本原文').isVisible(), '事件流缺少本软件派生说明')
  await click(page.getByRole('button', {name:'查看界面采集日志'}))
  await page.getByText('受控界面日志').waitFor({timeout:15000})
  check(await page.getByText('受控界面日志').isVisible(), '脚本界面日志不可见')
  await click(page.getByRole('button', {name:'查看原始日志'}))
  await page.getByText('MaaAssistantArknights GUI started').waitFor({timeout:15000})
  check(await page.getByText('MaaAssistantArknights GUI started').isVisible(), '原始日志不可见')
  await click(page.getByRole('button', {name:'返回脚本界面日志'}))
  await page.getByText('受控界面日志').waitFor({timeout:15000})
  check(await page.getByText('受控界面日志').isVisible(), '切回界面日志失败')
  await click(page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}))
  await page.getByLabel('清理明细截止日期').fill('2099-01-01')
  await click(page.getByRole('button',{name:'清理详细日志'}))
  await page.getByRole('alertdialog').waitFor({timeout:15000})
  check(await page.getByRole('alertdialog').isVisible(), '未显示清理确认')
  await click(page.getByRole('alertdialog').getByRole('button',{name:'取消'}))
  await click(page.getByRole('button', {name:'查看界面采集日志'}))
  await page.getByText('受控界面日志').waitFor({timeout:15000})
  check(await page.getByText('受控界面日志').isVisible(), '取消清理后详细日志丢失')
  await click(page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}))
  await click(page.getByRole('button',{name:'清理详细日志'}))
  await click(page.getByRole('alertdialog').getByRole('button',{name:'确认清理'}))
  await page.getByText('已清理 1 条详细日志。').waitFor({timeout:15000})
  check(await record('旧验收记录').isVisible(), '清理明细后运行记录丢失')
  await click(page.getByRole('button',{name:'查看本轮运行日志'}))
  await page.getByText('受控本轮日志：总耗时 12 秒').waitFor({timeout:15000})
  await click(page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}))
  check(await page.getByText('本轮日志摘要').isVisible(), '实时摘要区域不可见')
  await page.getByLabel('开始日期').fill('2099-12-31')
  await page.getByLabel('结束日期').fill('2099-01-01')
  await click(page.getByRole('button',{name:'搜索',exact:true}))
  check(await page.getByText('开始日期不能晚于结束日期。').isVisible(),'无效日期未提示')
  check(await record('旧验收记录').isVisible(),'无效筛选改变了结果')
  await click(page.getByRole('button',{name:'重置',exact:true}))
  check(await record('今日验收记录').isVisible(),'重置未显示今日记录')
  check(await record('旧验收记录').count() === 0,'重置后仍显示旧记录')
  await page.addInitScript({content:`
    class FixtureSocket {
      static CONNECTING = 0; static OPEN = 1; readyState = 1
      constructor() { window.__fixtureSocket = this; setTimeout(() => this.onopen?.(), 0) }
      send() {} close() { this.readyState = 3 }
    }
    window.WebSocket = FixtureSocket
  `})
  await page.reload(); await click(page.getByRole('button', { name: '日志', exact: true }))
  await page.waitForFunction(() => !!window.__fixtureSocket)
  await page.evaluate(() => {
    const socket = window.__fixtureSocket
    const emit = (type, data) => socket.onmessage({data: JSON.stringify({type, data})})
    emit('runtime', { busy: true, activeTaskId: 'fixture', cooldownSeconds: 0, run: { id: 'fixture-run', queue_name: '受控运行', state: 'running', items: [{ task_id:'fixture', task_name:'MAA', state:'waiting_manual' }] }, runCenter: { currentScriptName: '受控脚本', currentTaskName: '受控任务', currentState: 'waiting_manual', currentIndex: 1, totalPrograms: 1, readableProgress:'正在等待人工确认' }, liveLogs: [
      { runId:'fixture-run', sequence:1, taskId:'fixture', message:'开始校验任务配置', phase:'validation' },
      { runId:'fixture-run', sequence:2, taskId:'fixture', message:'已启动受管进程 PID 123', phase:'running' },
      { runId:'fixture-run', sequence:3, taskId:'fixture', message:'等待人工确认当前程序完成', phase:'waiting' },
    ] })
  })
  check(await page.locator('.side-live-list').getByText('等待人工确认当前程序完成').isVisible(), '确认完成前未从运行快照恢复等待摘要')
  check(await page.locator('.side-live-list .log-phase-validation').count() > 0, '运行快照未保留校验阶段')
  check(await page.locator('.side-live-list .log-phase-waiting').count() > 0, '运行快照未保留等待确认阶段')
  check(await page.getByText('正在等待人工确认').isVisible(), '运行中的可读进度未显示')
  await page.evaluate(() => {
    const socket = window.__fixtureSocket
    for (let i = 1; i <= 41; i++) socket.onmessage({data: JSON.stringify({type:'log', data:{message:`受控消息 ${i}`}})})
  })
  check(await page.locator('.side-live-list li').count() === 40, '实时日志数量错误')
  check((await page.locator('.side-live-list li').first().textContent()).includes('受控消息 2'), '实时日志保留顺序错误')
  await page.evaluate(() => window.__fixtureSocket.onmessage({data: JSON.stringify({type:'log', data:{runId:'fixture-run',sequence:45,taskId:'fixture',message:'已启动受管进程 PID 123',phase:'running'}})}))
  check(await page.locator('.side-live-list').getByText('已启动受管程序 MAA').count() === 1, '实时日志未显示程序名称')
  check(await page.locator('.log-phase-running').count() > 0, '实时日志未标示运行阶段')
  await page.evaluate(() => {
    const socket = window.__fixtureSocket
    const send = (type, data) => socket.onmessage({data: JSON.stringify({type, data})})
    send('runtime', { busy: true, activeTaskId: 'fixture', cooldownSeconds: 0, run: { id: 'fixture-run', queue_name: '受控运行', state: 'running', items: [{ task_id:'fixture', task_name:'MAA', state:'waiting_manual', ui_log_status:'已连接受控日志' }] }, runCenter: { currentScriptName:'受控脚本', currentTaskName:'MAA', currentState:'waiting_manual', currentIndex:1, totalPrograms:1 } })
    send('script_log', {runId:'fixture-run', taskId:'fixture', sourceId:'fixture-external', sourceName:'fixture.log', sourceType:'external', sequence:1, append:false, text:'受控脚本实时日志', truncated:false, updatedAt:'2026-10-23T00:00:00Z'})
    send('script_log', {runId:'fixture-run', taskId:'fixture', sourceId:'fixture-window', sourceName:'界面读取', sourceType:'window', sequence:1, append:false, text:'受控界面来源日志', truncated:false, updatedAt:'2026-10-23T00:00:01Z'})
  })
  check(await page.locator('.side-live-list').getByText('受控脚本实时日志').isVisible(), '脚本实时日志不可见')
  check(await page.locator('.script-source-tabs').getByText('fixture.log').isVisible(), '脚本日志来源标签不可见')
  await click(page.locator('.script-source-tabs').getByText('fixture.log'))
  await page.evaluate(() => window.__fixtureSocket.onmessage({data: JSON.stringify({type:'script_log', data:{runId:'fixture-run', taskId:'fixture', sourceId:'fixture-window', sourceName:'界面读取', sourceType:'window', sequence:2, append:true, text:'\n受控界面来源新增日志', truncated:false, updatedAt:'2026-10-23T00:00:02Z'}})}))
  check(await page.locator('.script-source-tabs button').filter({hasText:'界面读取'}).locator('i').count() === 1, '未选中来源新增内容后没有活动标记')
  await click(page.locator('.script-source-tabs').getByText('界面读取'))
  check(await page.locator('.script-source-tabs button').filter({hasText:'界面读取'}).locator('i').count() === 0, '查看来源后活动标记没有清除')
  await click(page.locator('.script-source-tabs').getByText('fixture.log'))
  check(await page.locator('.scheduler-live-list').getByText('受控消息 41').isVisible(), '脚本运行时本软件日志不可见')
  await page.evaluate(() => {
    const socket = window.__fixtureSocket
    const emit = (type, data) => socket.onmessage({data: JSON.stringify({type, data})})
    emit('script_log', {runId:'fixture-run', taskId:'fixture', sourceId:'fixture-external', sourceName:'fixture.log', sourceType:'external', sequence:2, append:true, text:'\n' + Array.from({length:60}, (_, index) => `受控脚本实时日志 ${index + 1}`).join('\n'), truncated:false, updatedAt:'2026-10-23T00:00:01Z'})
  })
  await page.locator('.script-live-list').getByText('受控脚本实时日志 60').waitFor({timeout:15000})
  await page.evaluate(() => {
    document.querySelector('.scheduler-live-list').scrollTop = 0
    document.querySelector('.script-live-list').scrollTop = 0
    const socket = window.__fixtureSocket
    socket.onmessage({data: JSON.stringify({type:'log', data:{message:'受控消息 42'}})})
    socket.onmessage({data: JSON.stringify({type:'script_log', data:{runId:'fixture-run', taskId:'fixture', sourceId:'fixture-external', sourceName:'fixture.log', sourceType:'external', sequence:3, append:true, text:'\n受控脚本实时日志 61', truncated:false, updatedAt:'2026-10-23T00:00:02Z'}})})
  })
  await page.locator('.script-live-list').getByText('受控脚本实时日志 61').waitFor({timeout:15000})
  for (const selector of ['.scheduler-live-list', '.script-live-list']) {
    check(await page.locator(selector).evaluate(el => el.scrollTop + el.clientHeight >= el.scrollHeight - 1), `${selector} 新日志后未自动滚至末尾`)
  }
  check(await page.locator('.side-live-log button').filter({hasText:/回到最新|回到底部|未读/}).count() === 0, '实时日志仍显示手动回到底部按钮')
  const before = await page.locator('.scheduler-live-list').boundingBox()
  const scriptBox = await page.locator('.script-live-list').boundingBox()
  check(before && scriptBox && before.y + before.height <= scriptBox.y, '摘要未位于脚本日志上方')
  await page.evaluate(() => window.__fixtureSocket.onmessage({data: JSON.stringify({type:'runtime', data:{busy:true,activeTaskId:null,cooldownSeconds:0,run:{id:'fixture-run',queue_name:'受控运行',state:'running',items:[]}}})}))
  await page.locator('.script-live-list').waitFor({state:'detached'})
  const between = await page.locator('.scheduler-live-list').boundingBox()
  check(Boolean(between), '脚本切换后本软件日志摘要不可见')
  await page.evaluate(() => window.__fixtureSocket.onmessage({data: JSON.stringify({type:'runtime', data:{busy:true,activeTaskId:'fixture',cooldownSeconds:0,run:{id:'fixture-run',queue_name:'受控运行',state:'running',items:[{task_id:'fixture',task_name:'MAA',state:'waiting_manual'}]},runCenter:{currentScriptName:'受控脚本',currentTaskName:'MAA',currentState:'waiting_manual',currentIndex:1,totalPrograms:1}}})}))
  await click(page.getByRole('button', {name:'脚本', exact:true}))
  check(await page.locator('.side-live-list').getByText('受控脚本实时日志').isVisible(), '跨页面脚本实时日志丢失')
  await click(page.getByRole('button', {name:'日志', exact:true}))
  await page.evaluate(() => window.__fixtureSocket.onmessage({data: JSON.stringify({type:'runtime', data:{ busy:true, activeTaskId:'fixture', cooldownSeconds:0, run:{id:'fixture-run',queue_name:'受控运行',state:'running',items:[{task_id:'fixture',task_name:'MAA',state:'running'}]},runCenter:{currentScriptName:'受控脚本',currentTaskName:'MAA',currentState:'waiting_manual',currentIndex:1,totalPrograms:1} }})}))
  await page.setViewportSize({width:1280,height:820})
  await page.locator('.content-body').evaluate(el => { const spacer = document.createElement('div'); spacer.style.height = '1000px'; spacer.setAttribute('aria-hidden', 'true'); el.appendChild(spacer); el.scrollTop = 120 })
  await page.waitForTimeout(150)
  check(await page.locator('.run-center').evaluate(el => el.classList.contains('is-compact')), '运行中心未进入紧凑状态')
  const centerBox = await page.locator('.run-center').boundingBox()
  const bodyBox = await page.locator('.content-body').boundingBox()
  check(centerBox && bodyBox && centerBox.y + centerBox.height <= bodyBox.y, '运行中心遮挡内容区域')
  check(await page.getByRole('button', {name:'确认当前程序完成'}).isVisible(), '人工确认入口不可见')
  await page.screenshot({path:'active-log-1280.png'})
  await click(page.getByRole('button', {name:'脚本', exact:true}))
  check(await page.locator('.side-live-list').getByText('受控消息 41').count() === 1, '跨页面实时日志丢失')
  await click(page.getByRole('button', {name:'日志', exact:true}))
  for (const width of [1080,960,640]) {
    await page.setViewportSize({width,height:800}); await page.waitForTimeout(200)
    check(!(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)), `${width}px 页面发生横向溢出`)
    await page.screenshot({path:`log-viewport-${width}.png`})
  }
  return 'PASS: 日志日期筛选、按日分组、失败详情、实时摘要、无效范围和重置'
}
