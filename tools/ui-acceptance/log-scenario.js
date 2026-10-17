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
  check(await page.getByText('本轮日志摘要').isVisible(), '实时摘要区域不可见')
  await page.getByLabel('开始日期').fill('2099-12-31')
  await page.getByLabel('结束日期').fill('2099-01-01')
  await page.getByRole('button',{name:'搜索',exact:true}).click()
  check(await page.getByText('开始日期不能晚于结束日期。').isVisible(),'无效日期未提示')
  check(await page.getByText('旧验收记录').isVisible(),'无效筛选改变了结果')
  await page.getByRole('button',{name:'重置',exact:true}).click()
  check(await page.getByText('今日验收记录').isVisible(),'重置未显示今日记录')
  check(await page.getByText('旧验收记录').count() === 0,'重置后仍显示旧记录')
  for (const width of [1080,960,640]) {
    await page.setViewportSize({width,height:800}); await page.waitForTimeout(200)
    check(!(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)), `${width}px 页面发生横向溢出`)
    await page.screenshot({path:`log-viewport-${width}.png`})
  }
  return 'PASS: 日志日期筛选、按日分组、失败详情、实时摘要、无效范围和重置'
}
