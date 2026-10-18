import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import UiLogRegionEditor from './UiLogRegionEditor.vue'
import type { Task } from './types'

const task = () => ({ id: 'task', executable: 'C:\\MAA\\MAA.exe', ui_log_region: null } as Task)
const windowCandidate = { handle: 3, pid: 12, created_at: 10, title: 'MAA', window_class: 'test', client_width: 200, client_height: 100 }
const region = { executable: 'C:\\MAA\\MAA.exe', window_class: 'test', client_width: 200, client_height: 100, x: 30, y: 5, width: 100, height: 80 }

describe('界面日志区域', () => {
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
})
