import type {
  PlaybackCapabilities,
  PlaybackController,
  PlaybackEventName,
  PlaybackSnapshot,
  PlaybackSource,
} from './types'
import { createStreamRecovery, type StreamRecoveryResult } from './streamRecovery.ts'

/**
 * 基于 <audio> 元素的实现：本地文件 / HTTP 直链 / app:// 都走这里。
 * 内部自己创建并管理 Audio 元素，外部只通过接口操作，不再拿 DOM ref。
 */
const CAPABILITIES: PlaybackCapabilities = {
  seek: true,
  duration: true,
  buffered: true,
  volume: true,
  audioData: false,
}

export function createMediaElementController(initialVolume = 0.8, options: {
  sourceError?: (url: string) => Promise<string | { message: string; recoverable: boolean }>
  recoverSource?: (url: string) => Promise<StreamRecoveryResult>
  recoveryDelayMs?: number
  errorCheckIntervalMs?: number
  log?: (event: string, details: Record<string, unknown>) => void
} = {}): PlaybackController {
  const el = new Audio()
  el.preload = 'metadata'
  el.volume = clamp01(initialVolume)

  let state: PlaybackSnapshot = {
    playing: false,
    loading: false,
    currentTime: 0,
    duration: 0,
    buffered: [],
    volume: clamp01(initialVolume),
    muted: false,
    error: null,
  }

  let objectUrl = ''
  let loadGeneration = 0
  let sourceUrl = ''
  let wantsPlay = false
  let needsRecovery = false
  let errorTimer: ReturnType<typeof setInterval> | undefined
  let diagnosticTimer: ReturnType<typeof setInterval> | undefined
  let lastPosition = 0, lastMovement = Date.now(), lastHeartbeat = Date.now(), reportedStall = false
  const listeners = new Map<PlaybackEventName, Set<() => void>>()
  const recovery = createStreamRecovery({
    renew: (url) => options.recoverSource!(url),
    delayMs: options.recoveryDelayMs,
    log: (event, details) => record(event, details),
    suspend() {
      ++loadGeneration
      stopErrorCheck()
      needsRecovery = true
      const time = state.currentTime
      el.pause()
      patch({ currentTime: time, playing: wantsPlay, loading: true, error: null })
      return time
    },
    async restore(url, position, signal) {
      sourceUrl = url
      try {
        const metadata = waitForMedia('loadedmetadata', signal)
        el.src = url
        el.load()
        await metadata
        const time = Math.min(position(), readDuration() || position())
        if (time > 0) {
          const seeking = waitForMedia('seeked', signal)
          el.currentTime = time
          await seeking
        }
        await withCancellation(el.play(), signal)
      } catch (error) {
        el.pause()
        if (!signal.aborted) {
          let failure
          try { failure = await options.sourceError?.(url) } catch { /* 诊断不可用时依靠媒体错误码 */ }
          if (!signal.aborted && typeof failure === 'object' && failure.message && !failure.recoverable) fail(failure.message)
          else if (!signal.aborted && el.error?.code === 3) fail('音频无法解码')
        }
        throw error
      }
    },
    resumed() {
      needsRecovery = false
      patch({ playing: true, loading: false, currentTime: el.currentTime, duration: readDuration(), buffered: readBuffered() }, 'play')
      startErrorCheck()
    },
    failed: (message) => fail(message),
  })

  function record(event: string, details: Record<string, unknown> = {}): void {
    if (!options.log) return
    try {
      options.log(event, {
        source: sourceUrl, generation: loadGeneration, currentTime: state.currentTime, duration: state.duration,
        playing: state.playing, loading: state.loading, buffered: state.buffered,
        readyState: el.readyState, networkState: el.networkState, mediaErrorCode: el.error?.code ?? 0,
        muted: el.muted, volume: el.volume, wantsPlay, recovering: recovery.active(), ...details,
      })
    } catch { /* 诊断不能干扰播放器 */ }
  }

  function stopDiagnostics(): void { if (diagnosticTimer !== undefined) clearInterval(diagnosticTimer); diagnosticTimer = undefined }
  function startDiagnostics(): void {
    stopDiagnostics()
    if (!options.log) return
    lastPosition = state.currentTime; lastMovement = lastHeartbeat = Date.now(); reportedStall = false
    diagnosticTimer = setInterval(() => {
      if (!wantsPlay || state.error) return
      const now = Date.now()
      if (Math.abs(state.currentTime - lastPosition) > 0.05) {
        if (reportedStall) record('progress_resumed', { stalledMs: now - lastMovement })
        lastMovement = now; lastPosition = state.currentTime; reportedStall = false
      } else if (now - lastMovement >= 10000 && !reportedStall) {
        record('progress_stalled', { stalledMs: now - lastMovement }); reportedStall = true
      }
      if (now - lastHeartbeat >= 30000) { record('heartbeat'); lastHeartbeat = now }
    }, 1000)
  }

  function withCancellation<T>(promise: Promise<T>, signal: AbortSignal): Promise<T> {
    return new Promise((resolve, reject) => {
      const finish = (error?: Error, value?: T) => {
        clearTimeout(timer); signal.removeEventListener('abort', cancel)
        if (error) reject(error); else resolve(value as T)
      }
      const cancel = () => finish(new Error('cancelled'))
      const timer = setTimeout(() => finish(new Error('加载超时')), 25000)
      signal.addEventListener('abort', cancel, { once: true })
      promise.then((value) => finish(undefined, value), (error) => finish(error))
      if (signal.aborted) cancel()
    })
  }

  function waitForMedia(event: string, signal: AbortSignal): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const cleanup = () => { clearTimeout(timer); el.removeEventListener(event, loaded); el.removeEventListener('error', error); signal.removeEventListener('abort', cancelled) }
      const loaded = () => { cleanup(); resolve() }
      const error = () => { cleanup(); reject(new Error('媒体加载中断')) }
      const cancelled = () => { cleanup(); reject(new Error('cancelled')) }
      const timer = setTimeout(() => { cleanup(); reject(new Error('加载超时')) }, 25000)
      el.addEventListener(event, loaded, { once: true }); el.addEventListener('error', error, { once: true })
      signal.addEventListener('abort', cancelled, { once: true })
      if (signal.aborted) cancelled()
    })
  }

  function handleFailure(message: string, recoverable = true): void {
    record('source_failure', { message, recoverable })
    if (recovery.active()) return
    if (recoverable && wantsPlay && sourceUrl.startsWith('stream://') && options.recoverSource) {
      void recovery.start(sourceUrl)
    } else fail(message)
  }

  function emit(event: PlaybackEventName): void {
    const set = listeners.get(event)
    if (!set) return
    for (const handler of set) {
      try {
        handler()
      } catch (error) {
        console.error(`[playback] ${event} 监听器异常`, error)
      }
    }
  }

  function patch(next: Partial<PlaybackSnapshot>, event?: PlaybackEventName): void {
    state = { ...state, ...next }
    emit('statechange')
    if (event) emit(event)
  }

  function readBuffered(): Array<[number, number]> {
    const ranges: Array<[number, number]> = []
    try {
      for (let i = 0; i < el.buffered.length; i += 1) {
        ranges.push([el.buffered.start(i), el.buffered.end(i)])
      }
    } catch {
      // buffered 在某些实现下会抛，忽略
    }
    return ranges
  }

  function readDuration(): number {
    return Number.isFinite(el.duration) ? el.duration : 0
  }

  function releaseObjectUrl(): void {
    if (!objectUrl) return
    URL.revokeObjectURL(objectUrl)
    objectUrl = ''
  }

  function stopErrorCheck(): void {
    if (errorTimer !== undefined) clearInterval(errorTimer)
    errorTimer = undefined
  }

  function fail(message: string): void {
    record('playback_error', { message })
    stopDiagnostics()
    recovery.cancel()
    wantsPlay = false
    ++loadGeneration
    stopErrorCheck()
    el.pause()
    patch({ error: message, loading: false, playing: false }, 'error')
  }

  function startErrorCheck(): void {
    stopErrorCheck()
    if (!options.sourceError || !sourceUrl.startsWith('stream://')) return
    const generation = loadGeneration
    const url = sourceUrl
    let pending = false
    errorTimer = setInterval(async () => {
      if (pending) return
      pending = true
      try {
        const failure = await options.sourceError!(url)
        const message = typeof failure === 'string' ? failure : failure.message
        if (generation === loadGeneration && message) handleFailure(message, typeof failure === 'string' || failure.recoverable)
      } catch {
        // 宿主诊断通道暂不可用时，仍由媒体本身报告错误。
      } finally { pending = false }
    }, options.errorCheckIntervalMs ?? 1000)
  }

  /* ---------- 元素事件 ---------- */

  el.addEventListener('loadedmetadata', () => {
    if (recovery.active()) return
    patch({ duration: readDuration(), currentTime: el.currentTime, loading: false }, 'loaded')
    record('metadata_loaded')
  })

  el.addEventListener('durationchange', () => {
    if (recovery.active()) return
    patch({ duration: readDuration() })
  })

  el.addEventListener('progress', () => {
    patch({ buffered: readBuffered() })
  })

  el.addEventListener('timeupdate', () => {
    if (state.error || recovery.active() || needsRecovery) return
    patch({ currentTime: el.currentTime, buffered: readBuffered() }, 'timeupdate')
  })

  el.addEventListener('play', () => { if (!recovery.active()) { wantsPlay = true; patch({ playing: true, loading: false }, 'play'); record('media_play') } })
  el.addEventListener('pause', () => { if (!recovery.active()) patch({ playing: false }, 'pause') })

  el.addEventListener('ended', () => {
    if (state.error || recovery.active() || needsRecovery) return
    wantsPlay = false
    record('media_ended')
    stopDiagnostics()
    stopErrorCheck()
    patch({ playing: false, currentTime: state.duration }, 'ended')
  })

  el.addEventListener('waiting', () => { if (!state.loading) record('buffering_start'); patch({ loading: true }) })
  el.addEventListener('stalled', () => record('network_stalled'))
  el.addEventListener('loadstart', () => patch({ loading: true }))
  el.addEventListener('canplay', () => { if (!recovery.active()) patch({ loading: false }) })
  el.addEventListener('playing', () => { if (!recovery.active()) { record('media_playing'); patch({ loading: false }) } })
  el.addEventListener('seeked', () => { if (!recovery.active()) patch({ currentTime: el.currentTime }) })

  el.addEventListener('error', () => {
    if (recovery.active()) return
    const code = el.error?.code
    record('media_error', { message: el.error?.message })
    const message = code ? `音频加载失败（错误码 ${code}）` : '音频加载失败'
    if ((code === 3 || code === 4) && options.sourceError && sourceUrl.startsWith('stream://')) {
      const generation = loadGeneration
      void options.sourceError(sourceUrl).then((failure) => {
        if (generation !== loadGeneration) return
        if (typeof failure === 'object' && failure.message) handleFailure(failure.message, failure.recoverable)
        else handleFailure(message, false)
      }, () => { if (generation === loadGeneration) handleFailure(message, false) })
    } else handleFailure(message)
  })

  /* ---------- 接口实现 ---------- */

  function load(source: PlaybackSource): void {
    if (sourceUrl) record('source_unload')
    recovery.cancel()
    wantsPlay = false
    needsRecovery = false
    ++loadGeneration
    stopErrorCheck()
    el.pause()
    releaseObjectUrl()

    patch({
      playing: false,
      loading: true,
      currentTime: 0,
      duration: 0,
      buffered: [],
      error: null,
    })

    if (source.file) objectUrl = URL.createObjectURL(source.file)
    const next = objectUrl || source.url || ''
    sourceUrl = next
    record('source_load', { localFile: Boolean(source.file) })
    startDiagnostics()

    if (next) {
      el.src = next
      el.load()
      startErrorCheck()
    } else {
      el.removeAttribute('src')
      el.load()
      patch({ loading: false })
    }
  }

  async function play(): Promise<void> {
    if (!el.src) return
    wantsPlay = true
    record('play_requested')
    startDiagnostics()
    if (needsRecovery) return recovery.start(sourceUrl)
    const generation = loadGeneration
    startErrorCheck()
    try {
      await el.play()
    } catch (error) {
      // 切歌或清空会终止旧的 play Promise；不能把它记为新曲目的错误。
      if (generation !== loadGeneration) return
      handleFailure(String(error))
    }
  }

  function pause(): void {
    record('pause_requested')
    stopDiagnostics()
    wantsPlay = false
    recovery.cancel()
    ++loadGeneration
    stopErrorCheck()
    el.pause()
    patch({ playing: false, loading: false })
  }

  function seek(seconds: number): void {
    if (!Number.isFinite(seconds)) return
    const max = readDuration() || seconds
    const next = Math.min(Math.max(seconds, 0), max)
    record('seek_requested', { targetTime: next })
    if (recovery.active() || needsRecovery) {
      recovery.seek(next)
      patch({ currentTime: next })
      return
    }
    el.currentTime = next
    patch({ currentTime: next })
  }

  function setVolume(value: number): void {
    const next = clamp01(value)
    el.volume = next
    el.muted = false
    patch({ volume: next, muted: false })
  }

  function setMuted(value: boolean): void {
    el.muted = value
    patch({ muted: value })
  }

  function dispose(): void {
    record('controller_dispose')
    stopDiagnostics()
    recovery.cancel()
    ++loadGeneration
    stopErrorCheck()
    try {
      el.pause()
      el.removeAttribute('src')
      el.load()
    } catch {
      // 忽略卸载期的异常
    }
    releaseObjectUrl()
    listeners.clear()
    el.remove()
  }

  function on(event: PlaybackEventName, handler: () => void): () => void {
    let set = listeners.get(event)
    if (!set) {
      set = new Set()
      listeners.set(event, set)
    }
    set.add(handler)
    return () => set?.delete(handler)
  }

  return {
    capabilities: CAPABILITIES,
    element: el,
    snapshot: () => state,
    on,
    load,
    play,
    pause,
    seek,
    setVolume,
    setMuted,
    dispose,
  }
}

function clamp01(value: number): number {
  return Math.min(Math.max(Number.isFinite(value) ? value : 0, 0), 1)
}
