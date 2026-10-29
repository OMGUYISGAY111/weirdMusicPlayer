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
}

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
  }
  const random = options.random ?? Math.random
  let player: PlaylistPlayer | null = null
  let generation = 0
  const listeners = new Set<(state: PlaylistState) => void>()
  const patch = (changes: Partial<PlaylistState>) => {
    state = { ...state, ...changes }
    for (const listener of listeners) listener(state)
  }
  const index = () => state.items.findIndex((item) => item.id === state.currentId)
  const hasError = () => state.status === 'error'

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
      await target.play()
      if (token === generation && !hasError() && !['buffering', 'paused'].includes(state.status)) patch({ status: 'playing' })
    } catch (error) {
      if (token === generation) patch({ status: 'error', error: error instanceof Error ? error.message : String(error) })
    }
  }

  function pause(): void {
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
    player?.pause()
    player?.load({})
    patch({ items, history, currentId: null, source: null, status: 'idle', error: '' })
    const successor = items[Math.min(oldIndex, items.length - 1)]
    if (successor && shouldContinue) void play(successor.id)
  }

  function clear(): void {
    ++generation
    player?.pause()
    player?.load({})
    patch({ items: [], history: [], currentId: null, source: null, status: 'idle', error: '' })
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
    patch({ status: 'error', error })
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
    add, updateMetadata, play, pause, resume, next, previous, ended, remove, clear, connect, fail, syncPlaying,
    setMode, canNext, canPrevious, syncBuffering,
  }
}
