import { afterEach, describe, expect, test, vi } from 'vitest'
import { useRunnerConnection } from './useRunnerConnection'

class FakeWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static instances: FakeWebSocket[] = []
  readyState = FakeWebSocket.CONNECTING
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onclose: ((event: CloseEvent) => void) | null = null
  onerror: (() => void) | null = null
  sent: string[] = []

  constructor(readonly url: string) { FakeWebSocket.instances.push(this) }
  open() { this.readyState = FakeWebSocket.OPEN; this.onopen?.() }
  close(code = 1000, reason = '') { this.readyState = 3; this.onclose?.({ code, reason } as CloseEvent) }
  send(payload: string) { this.sent.push(payload) }
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  FakeWebSocket.instances = []
})

describe('useRunnerConnection', () => {
  test('keeps REST commands available while a single realtime reconnect is pending', async () => {
    vi.useFakeTimers()
    vi.spyOn(Math, 'random').mockReturnValue(0)
    vi.stubGlobal('WebSocket', FakeWebSocket)
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ status: 'ok' }), text: async () => '' })))
    window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:38163', wsBase: 'ws://127.0.0.1:38163' }), selectExecutable: async () => ({ canceled: true }) }
    const refresh = vi.fn(async () => undefined)
    const manager = useRunnerConnection(refresh, vi.fn())

    await manager.connect()
    expect(FakeWebSocket.instances).toHaveLength(1)
    FakeWebSocket.instances[0].open()
    expect(manager.restOnline.value).toBe(true)
    expect(manager.realtimeState.value).toBe('online')
    expect(manager.diagnostic.value.lastHealthyAt).toBeTruthy()

    FakeWebSocket.instances[0].close(1006, 'network lost')
    expect(manager.restOnline.value).toBe(true)
    expect(manager.realtimeState.value).toBe('reconnecting')
    await expect(manager.request('/api/start', { method: 'POST' })).resolves.toEqual({ status: 'ok' })

    await vi.advanceTimersByTimeAsync(2_000)
    expect(FakeWebSocket.instances).toHaveLength(2)
    FakeWebSocket.instances[1].open()
    FakeWebSocket.instances[0].close(1006, 'late close')
    await vi.advanceTimersByTimeAsync(10_000)
    expect(FakeWebSocket.instances).toHaveLength(2)
    expect(manager.diagnostic.value.retryCount).toBe(0)
    manager.dispose()
  })

  test('marks commands unavailable only after the REST request fails', async () => {
    vi.stubGlobal('WebSocket', FakeWebSocket)
    let healthy = true
    vi.stubGlobal('fetch', vi.fn(async () => healthy ? ({ ok: true, json: async () => ({ status: 'ok' }), text: async () => '' }) : Promise.reject(new Error('backend stopped'))))
    window.desktopRunner = { getConnection: async () => ({ apiBase: 'http://127.0.0.1:38163', wsBase: 'ws://127.0.0.1:38163' }), selectExecutable: async () => ({ canceled: true }) }
    const manager = useRunnerConnection(async () => undefined, vi.fn())

    await manager.connect()
    healthy = false
    await expect(manager.request('/api/runtime')).rejects.toThrow('backend stopped')
    expect(manager.restOnline.value).toBe(false)
    manager.dispose()
  })
})
