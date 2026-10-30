import type { PlaybackSource } from '../playback/types'

/** 列表只保存原始来源；视频临时播放地址在播放时解析。 */
export interface PlaylistTrack {
  id: string
  title: string
  artist?: string
  cover?: string
  source: { kind: 'bilibili' | 'youtube'; url: string } | ({ kind: 'audio' } & PlaybackSource)
}

export interface PlaylistPlayer {
  load(source: PlaybackSource): void
  play(): Promise<void>
  pause(): void
  /** 当前播放进度（秒），重试同一首时用来回到原来的位置。 */
  position?(): number
  seek?(seconds: number): void
}

export type PlaybackMode = 'sequential' | 'repeat-all' | 'repeat-one' | 'shuffle'

/** 可恢复的数据，不含播放状态、临时流地址和媒体进度。 */
export interface PlaylistData {
  items: readonly PlaylistTrack[]
  currentId: string | null
  mode: PlaybackMode
  history: readonly string[]
}

export interface PlaylistState {
  items: readonly PlaylistTrack[]
  currentId: string | null
  status: 'idle' | 'resolving' | 'loading' | 'buffering' | 'playing' | 'paused' | 'ended' | 'error'
  error: string
  source: PlaybackSource | null
  mode: PlaybackMode
  /** 最近播放记录，随机播放时用于返回上一首。 */
  history: readonly string[]
  /** 排队中的自动重试倒计时（秒）；0 表示没有自动重试。 */
  autoRetryIn: number
}

/** 自动重试的等待秒数：5s、10s、20s，用完就需要用户手动重试。 */
const AUTO_RETRY_DELAYS = [5, 10, 20] as const

/** 无 Vue、宿主或组件依赖。解析器和播放端均由调用方注入。 */
export function createPlaylist(
  resolve: (track: PlaylistTrack) => Promise<PlaybackSource>,
  options: { random?: () => number; initial?: PlaylistData } = {},
) {
  const initial = options.initial
  let state: PlaylistState = {
    items: initial?.items.map((item) => ({ ...item, source: { ...item.source } })) ?? [],
    currentId: initial?.currentId ?? null,
    status: initial?.currentId ? 'paused' : 'idle',
    error: '', source: null,
    mode: initial?.mode ?? 'sequential',
    history: initial ? [...initial.history] : [],
    autoRetryIn: 0,
  }
  const random = options.random ?? Math.random
  let player: PlaylistPlayer | null = null
  let generation = 0
  /** 出错时的播放进度；重试同一首时从这里继续（切歌/正常播放/播完后清空）。 */
  let retryAt = 0
  let autoRetryTimer: ReturnType<typeof setInterval> | undefined
  let autoRetryAttempt = 0
  /** 正在由自动重试触发 play()，此时不能把手动重试的计数清零。 */
  let autoRetrying = false
  const listeners = new Set<(state: PlaylistState) => void>()
  const patch = (changes: Partial<PlaylistState>) => {
    state = { ...state, ...changes }
    for (const listener of listeners) listener(state)
  }
  const index = () => state.items.findIndex((item) => item.id === state.currentId)
  const hasError = () => state.status === 'error'

  /** 取消排队中的自动重试；reset 为真时同时清零重试次数（用户手动操作或播放成功）。 */
  function stopAutoRetry(reset = true): void {
    if (autoRetryTimer !== undefined) {
      clearInterval(autoRetryTimer)
      autoRetryTimer = undefined
    }
    if (state.autoRetryIn !== 0) patch({ autoRetryIn: 0 })
    if (reset) autoRetryAttempt = 0
  }

  /** 出错后按 5/10/20 秒自动重试同一首（play() 会 seek 回 retryAt）；用完后保留错误等用户手动重试。 */
  function scheduleAutoRetry(message: string): void {
    if (autoRetryTimer !== undefined) {
      clearInterval(autoRetryTimer)
      autoRetryTimer = undefined
    }
    const delay = AUTO_RETRY_DELAYS[autoRetryAttempt]
    if (delay === undefined) {
      patch({ status: 'error', error: message, autoRetryIn: 0 })
      return
    }
    autoRetryAttempt += 1
    let left = delay
    patch({ status: 'error', error: message, autoRetryIn: left })
    autoRetryTimer = setInterval(() => {
      left -= 1
      if (left > 0) {
        patch({ autoRetryIn: left })
        return
      }
      if (autoRetryTimer !== undefined) {
        clearInterval(autoRetryTimer)
        autoRetryTimer = undefined
      }
      patch({ autoRetryIn: 0 })
      autoRetrying = true
      void play().finally(() => { autoRetrying = false })
    }, 1000)
  }

  function add(track: PlaylistTrack): string {
    if (!state.items.some((item) => item.id === track.id)) {
      patch({ items: [...state.items, { ...track, source: { ...track.source } }] })
    }
    return track.id
  }

  function updateMetadata(id: string, metadata: Pick<PlaylistTrack, 'title' | 'artist' | 'cover'>): void {
    const existing = state.items.find((item) => item.id === id)
    if (!existing) return
    const updated = {
      ...existing,
      title: metadata.title || existing.title,
      artist: metadata.artist || existing.artist,
      cover: metadata.cover || existing.cover,
    }
    if (updated.title === existing.title && updated.artist === existing.artist && updated.cover === existing.cover) return
    patch({ items: state.items.map((item) => item.id === id ? updated : item) })
  }

  async function play(id = state.currentId ?? state.items[0]?.id): Promise<void> {
    const track = state.items.find((item) => item.id === id)
    if (!track) return
    // 用户主动播放：取消自动重试并重新计数；自动重试触发的 play 保留计数。
    if (!autoRetrying) stopAutoRetry(true)
    // 同一首在出错后重试：回到失败时的进度，而不是从 0 开始。
    // 进度存在 retryAt 里，这样即使重试时解析也失败（比如 60s 超时），下一次重试仍能接上。
    const retry = track.id === state.currentId && state.status === 'error'
    if (!retry) retryAt = 0
    const resumeAt = retry ? retryAt : 0
    const token = ++generation
    const target = player
    target?.pause()
    const history = state.history.at(-1) === track.id ? state.history : [...state.history, track.id].slice(-100)
    patch({ currentId: track.id, status: 'resolving', error: '', source: null, history })
    try {
      if (!target) throw new Error('播放器尚未连接')
      const source = await resolve(track)
      if (token !== generation) return
      if (!source.file && !source.url) throw new Error('没有可播放的音频地址')
      patch({ source, status: 'loading' })
      target.load(source)
      if (resumeAt > 0) target.seek?.(resumeAt)
      await target.play()
      if (token === generation && resumeAt > 0) {
        // 有的流会丢掉加载前设置的起播位置，播起来后校正一次。
        if (Math.abs((target.position?.() ?? 0) - resumeAt) > 1) target.seek?.(resumeAt)
      }
      if (token === generation && !hasError() && !['buffering', 'paused'].includes(state.status)) {
        patch({ status: 'playing' })
        retryAt = 0
        stopAutoRetry(true)
      }
    } catch (error) {
      if (token !== generation) return
      const message = error instanceof Error ? error.message : String(error)
      // 播放器都没连上时自动重试没有意义，直接把错误留给界面。
      if (target) scheduleAutoRetry(message)
      else patch({ status: 'error', error: message, autoRetryIn: 0 })
    }
  }

  function pause(): void {
    stopAutoRetry(true)
    ++generation
    player?.pause()
    patch({ status: state.currentId ? 'paused' : 'idle' })
  }

  async function resume(): Promise<void> {
    if (!state.currentId) return play()
    if (!state.source || !player || state.status === 'error' || state.status === 'ended') return play()
    const token = ++generation
    patch({ status: 'loading' })
    try {
      await player.play()
      if (token === generation && !hasError() && !['buffering', 'paused'].includes(state.status)) patch({ status: 'playing' })
    } catch (error) {
      if (token === generation) fail(String(error))
    }
  }

  function setMode(mode: PlaybackMode): void {
    patch({ mode })
  }

  function nextId(): string | undefined {
    if (state.mode === 'shuffle') {
      const candidates = state.items.filter((item) => item.id !== state.currentId)
      if (!candidates.length) return state.items[0]?.id
      return candidates[Math.floor(random() * candidates.length)]?.id
    }
    return state.items[index() + 1]?.id ?? (state.mode !== 'sequential' ? state.items[0]?.id : undefined)
  }

  function previousId(): string | undefined {
    if (state.mode === 'shuffle') return state.history.at(-2)
    if (index() < 0) return undefined
    return state.items[index() - 1]?.id ?? (state.mode !== 'sequential' ? state.items.at(-1)?.id : undefined)
  }

  function canNext(): boolean {
    return state.items.length > 0 && (state.mode !== 'sequential' || index() < state.items.length - 1)
  }

  function canPrevious(): boolean {
    return previousId() !== undefined
  }

  function next(): Promise<void> {
    const id = nextId()
    return id ? play(id) : Promise.resolve()
  }

  function previous(): Promise<void> {
    const id = previousId()
    if (!id) return Promise.resolve()
    if (state.mode === 'shuffle') patch({ history: state.history.slice(0, -1) })
    return play(id)
  }

  async function ended(): Promise<void> {
    if (state.status !== 'playing' && state.status !== 'loading') return
    // 播放端只通知结束；是否重播、随机切歌或停止完全由列表模式决定。
    const id = state.mode === 'repeat-one' ? state.currentId : nextId()
    retryAt = 0
    stopAutoRetry(true)
    patch({ status: 'ended' })
    if (id) await play(id)
  }

  function remove(id: string): void {
    const oldIndex = index()
    const isCurrent = id === state.currentId
    const shouldContinue = ['playing', 'loading', 'buffering', 'resolving'].includes(state.status)
    const items = state.items.filter((item) => item.id !== id)
    const history = state.history.filter((entry) => entry !== id)
    if (!isCurrent) { patch({ items, history }); return }
    ++generation
    retryAt = 0
    stopAutoRetry(true)
    player?.pause()
    player?.load({})
    patch({ items, history, currentId: null, source: null, status: 'idle', error: '' })
    const successor = items[Math.min(oldIndex, items.length - 1)]
    if (successor && shouldContinue) void play(successor.id)
  }

  function clear(): void {
    ++generation
    retryAt = 0
    stopAutoRetry(true)
    player?.pause()
    player?.load({})
    patch({ items: [], history: [], currentId: null, source: null, status: 'idle', error: '' })
  }

  /** 停掉自动重试定时器并清空订阅（应用关闭/组件卸载时调用）。 */
  function dispose(): void {
    stopAutoRetry(true)
    listeners.clear()
  }

  function connect(target: PlaylistPlayer): () => void {
    player = target
    return () => {
      if (player !== target) return
      pause()
      player = null
      patch({ source: null })
    }
  }

  function fail(error: string): void {
    if (!state.source) return
    // 断流时记下进度；即使之后重试的解析也失败，再次重试仍回到这里。
    retryAt = Math.max(retryAt, player?.position?.() ?? 0)
    // 先自动重试（5/10/20 秒），成功后从 retryAt 续播；用完后保留错误等用户手动重试。
    scheduleAutoRetry(error)
  }

  function syncPlaying(playing: boolean): void {
    if (['loading', 'buffering', 'playing', 'paused'].includes(state.status) || (playing && state.status === 'ended')) {
      patch({ status: playing ? 'playing' : 'paused' })
    }
  }

  function syncBuffering(loading: boolean, playing: boolean): void {
    if (!state.currentId || !['loading', 'buffering', 'playing'].includes(state.status)) return
    if (loading && playing) patch({ status: 'buffering' })
    else if (!loading && state.status === 'buffering') patch({ status: playing ? 'playing' : 'paused' })
  }

  return {
    snapshot: () => state,
    subscribe(listener: (state: PlaylistState) => void) {
      listeners.add(listener)
      return () => { listeners.delete(listener) }
    },
    add, updateMetadata, play, pause, resume, next, previous, ended, remove, clear, dispose, connect, fail, syncPlaying,
    setMode, canNext, canPrevious, syncBuffering,
  }
}
