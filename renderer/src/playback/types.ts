/**
 * 播放控制器的统一契约。
 *
 * 组件（AudioPlayer.vue）只依赖这个接口，不直接碰媒体实现：
 *   - LocalFile / HttpStream -> MediaElementController（<audio> 元素）
 *   - HLS / DASH           -> 将来的 MseController（hls.js / dash.js）
 *   - YouTube              -> 将来的 IframeController（postMessage）
 *
 * 每种实现用 capabilities 声明自己能做什么，UI 据此降级
 * （例如 iframe 实现没有 buffered，就不画缓冲条；live 流 duration=false 就显示 LIVE）。
 */

export type PlaybackEventName =
  | 'loaded'
  | 'timeupdate'
  | 'play'
  | 'pause'
  | 'ended'
  | 'statechange'
  | 'error'

export interface PlaybackCapabilities {
  /** 能否精确 seek（live 流通常 false） */
  readonly seek: boolean
  /** 是否有确定的时长（live 流为 false，duration 可能是 Infinity） */
  readonly duration: boolean
  /** 能否拿到缓存的缓冲区段 */
  readonly buffered: boolean
  /** 能否控制音量 */
  readonly volume: boolean
  /** 能否拿到音频数据（频谱/可视化需要） */
  readonly audioData: boolean
}

export interface PlaybackSnapshot {
  playing: boolean
  loading: boolean
  currentTime: number
  /** 秒；未知或直播时可能是 Infinity */
  duration: number
  /** 已缓冲区间 [start, end]，单位秒 */
  buffered: Array<[number, number]>
  volume: number
  muted: boolean
  error: string | null
}

export interface PlaybackSource {
  /** 本地文件（优先级高于 url） */
  file?: File | null
  /** 媒体地址：http(s) / app:// / blob: */
  url?: string
}

export interface PlaybackController {
  readonly capabilities: PlaybackCapabilities
  /** 底层 <audio> 元素；非媒体元素实现（iframe 等）返回 null */
  readonly element: HTMLAudioElement | null

  snapshot(): PlaybackSnapshot
  on(event: PlaybackEventName, handler: () => void): () => void

  load(source: PlaybackSource): void
  play(): Promise<void>
  pause(): void
  seek(seconds: number): void
  setVolume(value: number): void
  setMuted(value: boolean): void
  dispose(): void
}
