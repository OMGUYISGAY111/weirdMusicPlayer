import type {
  PlaybackCapabilities,
  PlaybackController,
  PlaybackEventName,
  PlaybackSnapshot,
  PlaybackSource,
} from './types'

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

export function createMediaElementController(initialVolume = 0.8): PlaybackController {
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
  const listeners = new Map<PlaybackEventName, Set<() => void>>()

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

  /* ---------- 元素事件 ---------- */

  el.addEventListener('loadedmetadata', () => {
    patch({ duration: readDuration(), currentTime: el.currentTime, loading: false }, 'loaded')
  })

  el.addEventListener('durationchange', () => {
    patch({ duration: readDuration() })
  })

  el.addEventListener('progress', () => {
    patch({ buffered: readBuffered() })
  })

  el.addEventListener('timeupdate', () => {
    patch({ currentTime: el.currentTime, buffered: readBuffered() }, 'timeupdate')
  })

  el.addEventListener('play', () => patch({ playing: true, loading: false }, 'play'))
  el.addEventListener('pause', () => patch({ playing: false }, 'pause'))

  el.addEventListener('ended', () => {
    patch({ playing: false, currentTime: state.duration }, 'ended')
  })

  el.addEventListener('waiting', () => patch({ loading: true }))
  el.addEventListener('loadstart', () => patch({ loading: true }))
  el.addEventListener('canplay', () => patch({ loading: false }))
  el.addEventListener('playing', () => patch({ loading: false }))
  el.addEventListener('seeked', () => patch({ currentTime: el.currentTime }))

  el.addEventListener('error', () => {
    const code = el.error?.code
    patch(
      { error: code ? `音频加载失败（错误码 ${code}）` : '音频加载失败', loading: false, playing: false },
      'error',
    )
  })

  /* ---------- 接口实现 ---------- */

  function load(source: PlaybackSource): void {
    ++loadGeneration
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

    if (next) {
      el.src = next
      el.load()
    } else {
      el.removeAttribute('src')
      el.load()
      patch({ loading: false })
    }
  }

  async function play(): Promise<void> {
    if (!el.src) return
    const generation = loadGeneration
    try {
      await el.play()
    } catch (error) {
      // 切歌或清空会终止旧的 play Promise；不能把它记为新曲目的错误。
      if (generation !== loadGeneration) return
      patch({ error: String(error), playing: false }, 'error')
    }
  }

  function pause(): void {
    ++loadGeneration
    el.pause()
  }

  function seek(seconds: number): void {
    if (!Number.isFinite(seconds)) return
    const max = readDuration() || seconds
    const next = Math.min(Math.max(seconds, 0), max)
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
    ++loadGeneration
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
