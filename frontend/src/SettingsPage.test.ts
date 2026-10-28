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
