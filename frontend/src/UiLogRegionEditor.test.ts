import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import UiLogRegionEditor from './UiLogRegionEditor.vue'
import type { Task } from './types'

const task = () => ({ id: 'task', executable: 'C:\\MAA\\MAA.exe', ui_log_region: null } as Task)
const windowCandidate = { handle: 3, pid: 12, created_at: 10, title: 'MAA', window_class: 'test', client_width: 200, client_height: 100 }
const region = { executable: 'C:\\MAA\\MAA.exe', window_class: 'test', client_width: 200, client_height: 100, x: 30, y: 5, width: 100, height: 80 }

describe('界面日志区域', () => {
  it('按预设提示可靠界面文本或画面识别', async () => {
    const model = task()
    const wrapper = mount(UiLogRegionEditor, { props: { task: model, busy: false, request: vi.fn() } })
    expect(wrapper.text()).toContain('可框选界面日志，使用本地画面识别')
    await wrapper.setProps({ task: { ...model, preset_id: 'march7th' } })
    expect(wrapper.text()).toContain('支持界面文本读取；框选识别可作为备用')
    await wrapper.setProps({ task: { ...model, preset_id: 'maa' } })
    expect(wrapper.text()).toContain('可框选界面日志，使用本地画面识别')
    wrapper.unmount()
  })

  it('显示可读的遮挡原因并清除旧预览', async () => {
    const model = task(); model.ui_log_region = region
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/windows')) return [windowCandidate]
      if (path.endsWith('/frame')) throw new Error('{"detail":"日志区域被遮挡或位于屏幕外"}')
      return { text: '旧日志' }
    })
    const wrapper = mount(UiLogRegionEditor, { props: { task: model, busy: false, request } })
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('画面预览暂停：日志区域被遮挡或位于屏幕外')
    expect(wrapper.text()).not.toContain('{"detail"')
    expect(wrapper.find('.visual-preview').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('旧日志')
    wrapper.unmount()
  })
  it('仅在预览后确认时保存区域', async () => {
    const model = task()
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/windows')) return [windowCandidate]
      if (path.endsWith('/select')) return { canceled: false, region }
      return { text: '测试日志' }
    })
    const wrapper = mount(UiLogRegionEditor, { props: { task: model, busy: false, request } })
    await wrapper.get('button').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '框选并预览')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('测试日志')
    expect(model.ui_log_region).toBeNull()
    await wrapper.findAll('button').find(button => button.text() === '确认并保存区域')!.trigger('click')
    expect(model.ui_log_region).toEqual(region)
    expect(wrapper.emitted('save')).toHaveLength(1)
  })

  it('预览失败时保留旧配置', async () => {
    const model = task(); model.ui_log_region = region
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/windows')) return [windowCandidate]
      if (path.endsWith('/select')) return { canceled: false, region }
      throw new Error('窗口被遮挡')
    })
    const wrapper = mount(UiLogRegionEditor, { props: { task: model, busy: false, request } })
    await wrapper.get('button').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '框选并预览')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('窗口被遮挡')
    expect(model.ui_log_region).toEqual(region)
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('重新选择窗口后展示保存的选区画面', async () => {
    const model = task(); model.ui_log_region = region
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/windows')) return [windowCandidate]
      if (path.endsWith('/frame')) return { image: 'cG5n' }
      return { text: '实时日志' }
    })
    const wrapper = mount(UiLogRegionEditor, { props: { task: model, busy: false, request } })
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(wrapper.find('.selection').exists()).toBe(true)
    expect(wrapper.get('.selection').attributes('style')).toContain('left: 15%')
    expect(wrapper.get('.selection').attributes('style')).toContain('top: 5%')
    expect(wrapper.get('.crop-frame img').attributes('style')).toContain('left: -30%')
    expect(wrapper.get('.crop-frame').attributes('style')).toContain('aspect-ratio: 200 / 100')
    expect(wrapper.get('.crop-image').attributes('style')).toContain('width: 62.5%')
    expect(wrapper.text()).toContain('实时日志')
    expect(model.ui_log_region).toEqual(region)
    wrapper.unmount()
  })
})
