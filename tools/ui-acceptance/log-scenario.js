async page => {
  const check = (ok, message) => { if (!ok) throw new Error(message) }
  await page.getByRole('button', { name: '日志', exact: true }).click()
  await page.getByText('今日验收记录').waitFor({timeout:15000})
  check(await page.getByText('验收计划跳过').isVisible(), '计划跳过信息缺失')
  check(await page.getByText('旧验收记录').count() === 0, '默认范围显示旧记录')
  await page.getByRole('button', { name: '最近三个月' }).click()
  await page.getByRole('button', { name: '搜索', exact: true }).click()
  check(await page.getByText('旧验收记录').isVisible(), '日期筛选未显示旧记录')
  await page.locator('.run-record').filter({hasText:'旧验收记录'}).getByRole('button',{name:'查看失败详情'}).click()
  check(await page.getByText('受控失败').isVisible(), '失败详情不可见')
  check(await page.getByText('受控摘要').isVisible(), '日志摘要不可见')
  await page.getByRole('button', {name:'查看详细日志'}).click()
  await page.getByText('受控详细内容').waitFor({timeout:15000})
  check(await page.getByText('受控详细内容').isVisible(), '详细日志不可见')
  await page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}).click()
  await page.getByLabel('清理明细截止日期').fill('2099-01-01')
  await page.getByRole('button',{name:'清理详细日志'}).click()
  await page.getByRole('alertdialog').waitFor({timeout:15000})
  check(await page.getByRole('alertdialog').isVisible(), '未显示清理确认')
  await page.getByRole('alertdialog').getByRole('button',{name:'取消'}).click()
  await page.getByRole('button', {name:'查看详细日志'}).click()
  await page.getByText('受控详细内容').waitFor({timeout:15000})
  check(await page.getByText('受控详细内容').isVisible(), '取消清理后详细日志丢失')
  await page.getByRole('dialog', {name:'详细日志'}).getByRole('button',{name:'关闭'}).click()
  check(await page.getByText('本轮日志摘要').isVisible(), '实时摘要区域不可见')
  await page.getByLabel('开始日期').fill('2099-12-31')
  await page.getByLabel('结束日期').fill('2099-01-01')
  await page.getByRole('button',{name:'搜索',exact:true}).click()
  check(await page.getByText('开始日期不能晚于结束日期。').isVisible(),'无效日期未提示')
  check(await page.getByText('旧验收记录').isVisible(),'无效筛选改变了结果')
  await page.getByRole('button',{name:'重置',exact:true}).click()
  check(await page.getByText('今日验收记录').isVisible(),'重置未显示今日记录')
  check(await page.getByText('旧验收记录').count() === 0,'重置后仍显示旧记录')
  await page.addInitScript(() => {
    class FixtureSocket {
      static CONNECTING = 0; static OPEN = 1; readyState = 1
      constructor() { window.__fixtureSocket = this; setTimeout(() => this.onopen?.(), 0) }
      send() {} close() { this.readyState = 3 }
    }
    window.WebSocket = FixtureSocket
  })
  await page.reload(); await page.getByRole('button', { name: '日志', exact: true }).click()
  await page.waitForFunction(() => !!window.__fixtureSocket)
  await page.evaluate(() => {
    const socket = window.__fixtureSocket
    const emit = (type, data) => socket.onmessage({data: JSON.stringify({type, data})})
    emit('runtime', { busy: true, activeTaskId: 'fixture', cooldownSeconds: 0, run: { id: 'fixture-run', queue_name: '受控运行', state: 'running', items: [] }, runCenter: { currentScriptName: '受控脚本', currentTaskName: '受控任务', currentState: 'waiting_manual', currentIndex: 1, totalPrograms: 1 } })
    for (let i = 1; i <= 41; i++) emit('log', { message: `受控消息 ${i}` })
  })
  check(await page.locator('.side-live-list li').count() === 40, '实时日志数量错误')
  check(await page.locator('.side-live-list li').first().textContent() === '受控消息 2', '实时日志保留顺序错误')
  await page.setViewportSize({width:1280,height:820})
  await page.locator('.content-body').evaluate(el => { const spacer = document.createElement('div'); spacer.style.height = '1000px'; spacer.setAttribute('aria-hidden', 'true'); el.appendChild(spacer); el.scrollTop = 120 })
  await page.waitForTimeout(150)
  check(await page.locator('.run-center').evaluate(el => el.classList.contains('is-compact')), '运行中心未进入紧凑状态')
  const centerBox = await page.locator('.run-center').boundingBox()
  const bodyBox = await page.locator('.content-body').boundingBox()
  check(centerBox && bodyBox && centerBox.y + centerBox.height <= bodyBox.y, '运行中心遮挡内容区域')
  check(await page.getByRole('button', {name:'确认当前程序完成'}).isVisible(), '人工确认入口不可见')
  await page.screenshot({path:'active-log-1280.png'})
  await page.getByRole('button', {name:'脚本', exact:true}).click()
  check(await page.locator('.side-live-list').getByText('受控消息 41').count() === 1, '跨页面实时日志丢失')
  await page.getByRole('button', {name:'日志', exact:true}).click()
  for (const width of [1080,960,640]) {
    await page.setViewportSize({width,height:800}); await page.waitForTimeout(200)
    check(!(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)), `${width}px 页面发生横向溢出`)
    await page.screenshot({path:`log-viewport-${width}.png`})
  }
  return 'PASS: 日志日期筛选、按日分组、失败详情、实时摘要、无效范围和重置'
}
