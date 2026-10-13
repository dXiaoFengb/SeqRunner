import { computed, ref } from 'vue'

export type RunnerConnection = { apiBase: string; wsBase: string }
export type RealtimeState = 'offline' | 'connecting' | 'online' | 'reconnecting'
export type ConnectionDiagnostic = {
  closeCode: number | null
  closeReason: string
  retryCount: number
  nextRetryAt: string | null
  lastHealthyAt: string | null
}

declare global {
  interface Window {
    desktopRunner?: {
      getConnection(): Promise<RunnerConnection | null>
      selectExecutable(presetId?: string | null): Promise<{ canceled: boolean; path?: string; matchesPreset?: boolean }>
      getBackendStatus?(): Promise<{ state: string; detail: string }>
      getBuildInfo?(): Promise<{ version: string }>
    }
  }
}

const HEARTBEAT_MS = 20_000
const HEARTBEAT_TIMEOUT_MS = 45_000
const FALLBACK_POLL_MS = 5_000
const MAX_RETRY_MS = 10_000

export function useRunnerConnection(refresh: () => Promise<void>, onEvent: (event: { type: string; data: any }) => void) {
  const restOnline = ref(false)
  const realtimeState = ref<RealtimeState>('offline')
  const diagnostic = ref<ConnectionDiagnostic>({ closeCode: null, closeReason: '', retryCount: 0, nextRetryAt: null, lastHealthyAt: null })
  let apiBase = ''
  let wsBase = ''
  let socket: WebSocket | undefined
  let retryTimer: number | undefined
  let heartbeatTimer: number | undefined
  let pollTimer: number | undefined
  let connectFlight: Promise<void> | undefined
  let generation = 0
  let disposed = false
  let lastRealtimeAt = 0

  const realtimeLabel = computed(() => ({
    offline: '实时不可用',
    connecting: '正在建立实时连接',
    online: '实时已连接',
    reconnecting: '实时连接重试中',
  })[realtimeState.value])

  function clearTimer(timer: number | undefined) { if (timer !== undefined) window.clearTimeout(timer) }
  function stopHeartbeat() { clearTimer(heartbeatTimer); heartbeatTimer = undefined }
  function stopPolling() { clearTimer(pollTimer); pollTimer = undefined }
  function startPolling() {
    if (pollTimer !== undefined || !restOnline.value || disposed) return
    const poll = async () => {
      pollTimer = undefined
      if (!restOnline.value || realtimeState.value === 'online' || disposed) return
      try { await refresh() } catch (error) { markRestOffline(error) }
      startPolling()
    }
    pollTimer = window.setTimeout(poll, FALLBACK_POLL_MS)
  }
  function heartbeat() {
    heartbeatTimer = undefined
    if (!socket || socket.readyState !== WebSocket.OPEN || realtimeState.value !== 'online') return
    if (Date.now() - lastRealtimeAt > HEARTBEAT_TIMEOUT_MS) {
      socket.close(4000, '心跳超时')
      return
    }
    socket.send(JSON.stringify({ type: 'ping', nonce: crypto.randomUUID() }))
    heartbeatTimer = window.setTimeout(heartbeat, HEARTBEAT_MS)
  }
  function startHeartbeat() { stopHeartbeat(); heartbeatTimer = window.setTimeout(heartbeat, HEARTBEAT_MS) }
  function retryDelay() { return Math.min(MAX_RETRY_MS, 1_000 * 2 ** Math.min(diagnostic.value.retryCount, 3)) + Math.floor(Math.random() * 250) }
  function scheduleRetry(reason: string, code: number | null = null) {
    if (disposed || retryTimer !== undefined) return
    diagnostic.value = { ...diagnostic.value, closeCode: code, closeReason: reason, retryCount: diagnostic.value.retryCount + 1 }
    const delay = retryDelay()
    diagnostic.value.nextRetryAt = new Date(Date.now() + delay).toISOString()
    realtimeState.value = restOnline.value ? 'reconnecting' : 'offline'
    startPolling()
    retryTimer = window.setTimeout(() => {
      retryTimer = undefined
      if (restOnline.value) openRealtime()
      else void connect()
    }, delay)
  }
  function markRestOffline(error: unknown) {
    restOnline.value = false
    realtimeState.value = 'offline'
    diagnostic.value = { ...diagnostic.value, closeReason: error instanceof Error ? error.message : String(error) }
    socket?.close(1000, 'REST 不可用')
    scheduleRetry(diagnostic.value.closeReason)
  }
  function openRealtime() {
    if (disposed || !wsBase || socket?.readyState === WebSocket.OPEN || socket?.readyState === WebSocket.CONNECTING) return
    stopHeartbeat()
    const ownGeneration = ++generation
    realtimeState.value = diagnostic.value.retryCount ? 'reconnecting' : 'connecting'
    const candidate = new WebSocket(`${wsBase}/ws`)
    socket = candidate
    candidate.onopen = () => {
      if (disposed || ownGeneration !== generation) return
      realtimeState.value = 'online'
      diagnostic.value = { closeCode: null, closeReason: '', retryCount: 0, nextRetryAt: null, lastHealthyAt: new Date().toISOString() }
      lastRealtimeAt = Date.now()
      stopPolling()
      startHeartbeat()
    }
    candidate.onmessage = (event) => {
      if (disposed || ownGeneration !== generation) return
      lastRealtimeAt = Date.now()
      try {
        const payload = JSON.parse(event.data) as { type: string; data: any }
        if (payload.type !== 'pong') onEvent(payload)
      } catch (error) {
        diagnostic.value = { ...diagnostic.value, closeReason: `实时消息格式错误：${String(error)}` }
      }
    }
    candidate.onerror = () => { /* close event carries the retry boundary */ }
    candidate.onclose = (event) => {
      if (disposed || ownGeneration !== generation) return
      stopHeartbeat()
      socket = undefined
      scheduleRetry(event.reason || '实时连接已关闭', event.code)
    }
  }
  async function connect() {
    if (connectFlight) return connectFlight
    connectFlight = (async () => {
      try {
        const connection = await window.desktopRunner?.getConnection()
        if (!connection) throw new Error('Electron 主进程没有提供本次后端连接。')
        apiBase = connection.apiBase
        wsBase = connection.wsBase
        const health = await fetch(`${apiBase}/health`)
        if (!health.ok) throw new Error(`后端健康检查失败（${health.status}）`)
        restOnline.value = true
        await refresh()
        openRealtime()
      } catch (error) {
        markRestOffline(error)
      }
    })().finally(() => { connectFlight = undefined })
    return connectFlight
  }
  async function request(path: string, options?: RequestInit) {
    if (!restOnline.value || !apiBase) throw new Error('本地后端未连接')
    try {
      const response = await fetch(`${apiBase}${path}`, options)
      if (!response.ok) throw new Error(await response.text())
      return response.json()
    } catch (error) {
      markRestOffline(error)
      throw error
    }
  }
  function dispose() {
    disposed = true
    clearTimer(retryTimer); retryTimer = undefined
    stopHeartbeat(); stopPolling()
    generation += 1
    socket?.close(1000, '页面已关闭')
    socket = undefined
  }
  return { restOnline, realtimeState, realtimeLabel, diagnostic, request, connect, dispose }
}
