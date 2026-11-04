import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, test, vi } from 'vitest'
import SettingsPage from './SettingsPage.vue'

const props = { settings: { autoStart:false, minimizeOnLaunch:false, notifications:{ completed:true, failed:true, system:true } }, saveState:'idle' as const, version:'v0.17.1', apiOnline:true, realtimeLabel:'已连接', diagnosticText:'', presets:[], saveSettings:vi.fn(), exportArchive:vi.fn(), refreshDiagnostics:vi.fn() }
afterEach(() => { document.body.innerHTML = ''; vi.useRealTimers(); vi.restoreAllMocks() })

test.each([[false,false,'none'],[true,true,'all'],[false,true,'failed'],[true,false,'legacy-success']] as const)('legacy notification conditions retain their value', async (completed, failed, expected) => {
  window.desktopRunner = undefined as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props:{...props,settings:{...props.settings,notifications:{completed,failed,system:true}}} }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click')
  expect((wrapper.get('[aria-label="推送任务结果时机"]').element as HTMLSelectElement).value).toBe(expected)
  wrapper.unmount()
})

test('QQ configuration dialog preserves an existing binding and returns focus', async () => {
  const unbind = vi.fn(); const cancel = vi.fn(); const enable = vi.fn()
  window.desktopRunner = { qqStatus:async () => ({bound:true,enabled:true,state:'connected'}), qqCancel:cancel, qqUnbind:unbind, qqEnable:enable } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); const opener = wrapper.get('[data-qq-channel]'); await opener.trigger('click'); await flushPromises()
  expect(document.querySelector('[role="dialog"]')?.textContent).toContain('已连接')
  expect(document.activeElement?.getAttribute('role')).toBe('dialog')
  document.querySelector<HTMLButtonElement>('[aria-label="关闭 QQ 配置"]')!.click(); await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull(); expect(document.activeElement).toBe(opener.element)
  expect(cancel).not.toHaveBeenCalled(); expect(unbind).not.toHaveBeenCalled(); expect(enable).not.toHaveBeenCalled()
  wrapper.unmount()
})

test('QR is inside dialog, refreshes, and closing cancels the session', async () => {
  const cancel = vi.fn(async () => undefined); const start = vi.fn(async () => ({id:'fixture', image:'data:image/png;base64,AA=='}))
  window.desktopRunner = { qqStatus:async () => ({bound:false,enabled:false,state:'unbound'}), qqCancel:cancel, qqStart:start, qqPoll:async () => ({state:'waiting'}) } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); await wrapper.get('[data-qq-channel]').trigger('click'); await flushPromises()
  const button = () => Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '扫码绑定')!
  button().click(); await flushPromises(); expect(document.querySelector('[role="dialog"] img[alt="手机 QQ 扫码绑定二维码"]')).not.toBeNull()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '刷新二维码')!.click(); await flushPromises(); expect(start).toHaveBeenCalledTimes(2)
  document.querySelector<HTMLElement>('[role="dialog"]')!.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull(); expect(cancel).toHaveBeenCalledTimes(3)
  wrapper.unmount()
})

test('binding can be disabled and requires explicit confirmation to unbind', async () => {
  let bound = true, enabled = true
  const unbind = vi.fn(async () => { bound = false; enabled = false })
  const enable = vi.fn(async (value:boolean) => { enabled = value })
  window.desktopRunner = { qqStatus:async () => ({bound,enabled,state:enabled?'connected':'disabled'}), qqUnbind:unbind, qqEnable:enable } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); await wrapper.get('[data-qq-channel]').trigger('click'); await flushPromises()
  const select = document.querySelector<HTMLSelectElement>('[aria-label="启用 QQ 通知"]')!; select.value = 'false'; select.dispatchEvent(new Event('change',{bubbles:true})); await flushPromises()
  expect(enable).toHaveBeenCalledWith(false); expect(document.querySelector('[role="dialog"]')?.textContent).toContain('已绑定，未启用')
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '解绑')!.click(); await flushPromises(); expect(unbind).not.toHaveBeenCalled()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="alertdialog"] button')).find(item => item.textContent === '确认解绑')!.click(); await flushPromises(); expect(unbind).toHaveBeenCalledTimes(1)
  wrapper.unmount()
})

test('closing while QR generation is pending cancels without reopening', async () => {
  let resolveStart!: (value:{id:string;image:string}) => void
  const cancel = vi.fn(async () => undefined)
  window.desktopRunner = { qqStatus:async () => ({bound:false,enabled:false,state:'unbound'}), qqCancel:cancel, qqStart:() => new Promise(resolve => { resolveStart = resolve }) } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); await wrapper.get('[data-qq-channel]').trigger('click'); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '扫码绑定')!.click(); await flushPromises()
  document.querySelector<HTMLButtonElement>('[aria-label="关闭 QQ 配置"]')!.click(); await flushPromises()
  resolveStart({id:'late',image:'data:image/png;base64,AA=='}); await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull(); expect(cancel).toHaveBeenCalledTimes(2)
  wrapper.unmount()
})

test('QR scan status progresses to binding success in the dialog', async () => {
  vi.useFakeTimers(); let bound = false; let count = 0
  window.desktopRunner = { qqStatus:async () => ({bound,enabled:bound,state:bound?'connecting':'unbound'}), qqCancel:async () => undefined, qqStart:async () => ({id:'fixture',image:'data:image/png;base64,AA=='}), qqPoll:async () => { count++; if (count === 1) return {state:'scanned'}; bound = true; return {state:'connecting'} } } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); await wrapper.get('[data-qq-channel]').trigger('click'); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '扫码绑定')!.click(); await flushPromises()
  await vi.advanceTimersByTimeAsync(2000); expect(document.querySelector('[role="dialog"]')?.textContent).toContain('已扫码')
  await vi.advanceTimersByTimeAsync(2000); expect(document.querySelector('[role="dialog"]')?.textContent).toContain('已绑定，正在连接')
  expect(document.querySelector('img[alt="手机 QQ 扫码绑定二维码"]')).toBeNull(); wrapper.unmount()
})

test('expired QR shows refresh without leaving the dialog', async () => {
  vi.useFakeTimers()
  window.desktopRunner = { qqStatus:async () => ({bound:false,enabled:false,state:'unbound'}), qqCancel:async () => undefined, qqStart:async () => ({id:'fixture',image:'data:image/png;base64,AA=='}), qqPoll:async () => ({state:'expired'}) } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click'); await wrapper.get('[data-qq-channel]').trigger('click'); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')).find(item => item.textContent === '扫码绑定')!.click(); await flushPromises()
  await vi.advanceTimersByTimeAsync(2000); expect(document.querySelector('[role="dialog"]')?.textContent).toContain('二维码已过期'); expect(document.querySelector('img[alt="手机 QQ 扫码绑定二维码"]')).toBeNull()
  wrapper.unmount()
})


test('Webhook dialog retains invalid and unsaved input and restores focus', async () => {
  const saveWebhook = vi.fn(async () => { throw new Error('地址无效') })
  window.desktopRunner = { listWebhooks:async () => [], saveWebhook } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click')
  const opener = wrapper.findAll('button').find(item => item.text() === '添加 Webhook')!
  await opener.trigger('click'); await flushPromises()
  const dialog = document.querySelector<HTMLElement>('.hook-dialog')!
  expect(document.activeElement).toBe(dialog)
  const input = dialog.querySelector<HTMLInputElement>('input')!; input.value = '示例'; input.dispatchEvent(new Event('input', { bubbles:true }))
  const button = (text:string) => Array.from(document.querySelectorAll<HTMLButtonElement>('.hook-dialog button')).find(el => el.textContent?.trim() === text)!
  button('保存').click(); await flushPromises()
  expect(saveWebhook).toHaveBeenCalledTimes(1); expect(dialog.textContent).toContain('地址无效'); expect(input.value).toBe('示例')
  dialog.dispatchEvent(new KeyboardEvent('keydown', { key:'Escape', bubbles:true })); await flushPromises()
  expect(document.activeElement?.textContent).toBe('取消'); expect(document.querySelector('[role="alertdialog"]')?.textContent).toContain('未保存')
  document.querySelector<HTMLButtonElement>('[role="alertdialog"] [data-cancel]')!.click(); await flushPromises(); expect(input.value).toBe('示例')
  button('取消').click(); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="alertdialog"] button')).find(el => el.textContent === '放弃更改')!.click(); await flushPromises()
  expect(document.querySelector('.hook-dialog')).toBeNull(); expect(document.activeElement).toBe(opener.element); wrapper.unmount()
})

test('Webhook save, edit and delete use existing services', async () => {
  let items: Array<{id:string;name:string;enabled:boolean;method:'POST'}> = []
  const full = {id:'1',name:'测试',enabled:false,method:'POST',url:'https://example.invalid',template:'{}',headers:{}}
  const saveWebhook = vi.fn(async () => { items = [{id:'1',name:'测试',enabled:false,method:'POST'}] })
  const removeWebhook = vi.fn(async () => { items = [] })
  window.desktopRunner = { listWebhooks:async () => items, getWebhook:async () => full, saveWebhook, removeWebhook } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises(); await wrapper.get('#settings-tab-notifications').trigger('click')
  await wrapper.findAll('button').find(el => el.text() === '添加 Webhook')!.trigger('click'); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('.hook-dialog button')).find(el => el.textContent === '保存')!.click(); await flushPromises()
  expect(document.querySelector('.hook-dialog')).toBeNull(); expect(wrapper.text()).toContain('测试')
  await wrapper.findAll('button').find(el => el.text() === '编辑')!.trigger('click'); await flushPromises()
  expect(document.querySelector<HTMLInputElement>('.hook-dialog input')?.value).toBe('测试')
  document.querySelector<HTMLButtonElement>('[aria-label="关闭 Webhook 配置"]')!.click(); await flushPromises()
  await wrapper.findAll('button').find(el => el.text() === '删除')!.trigger('click'); await flushPromises()
  expect(removeWebhook).not.toHaveBeenCalled(); expect(document.activeElement?.textContent).toBe('取消')
  Array.from(document.querySelectorAll<HTMLButtonElement>('[role="alertdialog"] button')).find(el => el.textContent === '确认删除')!.click(); await flushPromises()
  expect(removeWebhook).toHaveBeenCalledWith('1'); expect(wrapper.text()).toContain('尚未添加 Webhook'); wrapper.unmount()
})

test('unreadable Webhook reset requires confirmation', async () => {
  const resetUnreadableWebhooks = vi.fn(async () => undefined)
  window.desktopRunner = { listWebhooks:async () => { throw new Error('无法读取') }, resetUnreadableWebhooks } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises(); await wrapper.get('#settings-tab-notifications').trigger('click')
  await wrapper.findAll('button').find(el => el.text() === '重新配置')!.trigger('click'); await flushPromises()
  expect(document.querySelector('[role="alertdialog"]')?.textContent).toContain('保留无法读取的旧加密文件'); expect(resetUnreadableWebhooks).not.toHaveBeenCalled()
  document.querySelector<HTMLButtonElement>('[role="alertdialog"] [data-cancel]')!.click(); await flushPromises(); expect(resetUnreadableWebhooks).not.toHaveBeenCalled(); wrapper.unmount()
})


test('Webhook dialog traps keyboard focus and stays open while saving', async () => {
  let finish!: () => void
  window.desktopRunner = { listWebhooks:async () => [], saveWebhook:() => new Promise<void>(resolve => { finish = resolve }) } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises(); await wrapper.get('#settings-tab-notifications').trigger('click')
  await wrapper.findAll('button').find(el => el.text() === '添加 Webhook')!.trigger('click'); await flushPromises()
  const dialog = document.querySelector<HTMLElement>('.hook-dialog')!
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([{}] as unknown as DOMRectList)
  const first = dialog.querySelector<HTMLButtonElement>('[aria-label="关闭 Webhook 配置"]')!
  const buttons = Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')); const last = buttons[buttons.length - 1]
  last.focus(); last.dispatchEvent(new KeyboardEvent('keydown', {key:'Tab', bubbles:true, cancelable:true})); expect(document.activeElement).toBe(first)
  first.dispatchEvent(new KeyboardEvent('keydown', {key:'Tab',shiftKey:true,bubbles:true,cancelable:true})); expect(document.activeElement).toBe(last)
  buttons.find(el => el.textContent?.trim() === '保存')!.click(); await flushPromises()
  expect(first.disabled).toBe(true); dialog.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true})); await flushPromises(); expect(document.querySelector('.hook-dialog')).not.toBeNull()
  finish(); await flushPromises(); expect(document.querySelector('.hook-dialog')).toBeNull(); wrapper.unmount()
})

test('Webhook refresh errors remain visible after saving', async () => {
  let reads = 0
  window.desktopRunner = {
    listWebhooks: async () => { if (++reads > 1) throw new Error('重新读取失败'); return [] },
    saveWebhook: async () => undefined,
  } as unknown as typeof window.desktopRunner
  const wrapper = mount(SettingsPage, { props, attachTo:document.body }); await flushPromises()
  await wrapper.get('#settings-tab-notifications').trigger('click')
  await wrapper.findAll('button').find(el => el.text() === '添加 Webhook')!.trigger('click'); await flushPromises()
  Array.from(document.querySelectorAll<HTMLButtonElement>('.hook-dialog button')).find(el => el.textContent === '保存')!.click(); await flushPromises()
  expect(wrapper.text()).toContain('重新读取失败')
  expect(wrapper.text()).not.toContain('Webhook 已保存。')
  wrapper.unmount()
})
