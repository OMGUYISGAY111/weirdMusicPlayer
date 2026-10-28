/**
 * 前端与 CEF 宿主（CefSharp / C#）之间的进程间通信桥。
 *
 * 约定（CefSharp 154 起）：
 *   - C# 侧 `HostApi` 通过 JavascriptObjectRepository 注册为 `hostApi`；
 *   - 绑定是**异步**的：页面必须先 `await CefSharp.BindObjectAsync('hostApi')`，
 *     之后 `window.hostApi` 才存在，且每个方法都返回 Promise；
 *   - 复杂结构统一返回 JSON 字符串，由前端解析，避免跨进程类型映射的坑；
 *   - 纯浏览器里 `pnpm dev:web` 也能跑，所有调用自动降级为本地模拟值。
 */
import { reactive } from 'vue'
import { supportedAudioTypes } from '../playback/supportedAudioTypes'
import type { StreamRecoveryResult } from '../playback/streamRecovery'

/** C# 侧 GetHostInfo() 返回的 JSON 结构 */
export interface HostInfo {
  appName: string
  hostVersion: string
  cefVersion: string
  chromiumVersion: string
  platform: string
  arch: string
  dotnetVersion: string
  isDev: boolean
  userDataPath: string
  /** 数据目录来自哪里：命令行 / 环境变量 / 便携模式 / 默认 LOCALAPPDATA */
  userDataSource: string
}

/** 与 C# `HostApi` 一一对应（方法名由 PascalCase 转 camelCase） */
export interface CefHostApi {
  getHostInfo(): Promise<string>
  echo(text: string): Promise<string>
  minimize(): Promise<void>
  toggleMaximize(): Promise<void>
  closeWindow(): Promise<void>
  openDevTools(): Promise<void>
  setZoomLevel(level: number): Promise<void>
  openUserDataFolder(): Promise<void>
  openLogFolder(): Promise<void>
  recordPlaybackLog(json: string): Promise<void>
  /**
   * 注册宿主代理的媒体地址：返回 `stream://media/<id>`，可直接当 src 用。
   * 用来播 http 源（prod 下 secure origin 不能直接加载 http 媒体）或注入请求头。
   */
  registerStream(url: string, headersJson: string): Promise<string>
  getStreamError(url: string): Promise<string>
  getStreamFailure(url: string): Promise<string>
  recoverStream(url: string, supportedTypesJson: string): Promise<string>
  /** 解析 B站 地址：返回 JSON 字符串（ok / title / artist / duration / streamUrl / error） */
  resolveBilibili(url: string, supportedTypesJson: string): Promise<string>
  /** 解析 YouTube 音轨，按解码能力选择直接流播放或实时转码 */
  resolveYouTube(url: string, supportedTypesJson: string): Promise<string>
  /** 按关键词搜 B站 视频，返回 MediaSearchResult 的 JSON 字符串 */
  searchBilibili(keyword: string, limit: number): Promise<string>
  /** 按关键词搜 YouTube 视频（yt-dlp ytsearchN），返回结构同 searchBilibili */
  searchYouTube(keyword: string, limit: number): Promise<string>
  /** 用系统默认浏览器打开 http(s) 外链（视频页），避免把 CEF 窗口导航走 */
  openExternalUrl(url: string): Promise<void>
}

/** 搜索来源 */
export type MediaSearchSource = 'bilibili' | 'youtube'

/** 宿主搜索结果里的单条视频 */
export interface MediaSearchItem {
  source: MediaSearchSource
  /** B站 是 BV 号，YouTube 是视频 ID */
  id: string
  title: string
  /** UP 主 / 频道名 */
  author: string
  /** 视频页地址（https） */
  url: string
  /** 封面地址，可能为空 */
  thumbnail: string
  /** 时长，秒；0 表示未知 */
  duration: number
  /** 播放量；YouTube 偶尔拿不到，为 0 */
  playCount: number
}

/** 一次搜索的结果信封（单个平台） */
export interface MediaSearchResult {
  ok: boolean
  source: MediaSearchSource
  /** 实际用的关键词（歌名 + 作者 + 专辑） */
  keyword: string
  items: MediaSearchItem[]
  error: string | null
}

/** B站 解析结果 */
export interface BilibiliResult {
  ok: boolean
  bvid: string | null
  title: string | null
  artist: string | null
  duration: number
  /** 可直接当 src 用的 stream:// 地址 */
  streamUrl: string | null
  /** 兼容旧返回结构；流播放不会生成缓存，固定为 false */
  cached: boolean
  /** 原始流直接播放，或按需实时转码；两者均不生成音频文件。 */
  playbackMode?: 'direct' | 'transcode'
  error: string | null
}

interface CefSharpBridge {
  BindObjectAsync(...names: string[]): Promise<unknown>
}

declare global {
  interface Window {
    /** 绑定成功后由 CefSharp 注入 */
    hostApi?: CefHostApi
    /** CefSharp 注入的全局对象，提供 BindObjectAsync */
    CefSharp?: CefSharpBridge
  }
}

/** 桥接状态，供 UI 响应式使用 */
export const hostBridge = reactive({
  ready: false,
  error: '' as string,
})

const MOCK_INFO: HostInfo = {
  appName: '音乐播放器（浏览器预览）',
  hostVersion: '0.0.0-dev',
  cefVersion: '未连接宿主',
  chromiumVersion: typeof navigator === 'undefined' ? 'unknown' : navigator.userAgent,
  platform: typeof navigator === 'undefined' ? 'unknown' : navigator.platform || 'unknown',
  arch: 'unknown',
  dotnetVersion: 'n/a',
  isDev: true,
  userDataPath: '（浏览器预览模式没有本地数据目录）',
  userDataSource: '浏览器预览',
}

/**
 * 在挂载 Vue 之前调用：绑定 CefSharp 对象。
 * 浏览器环境直接返回 false，不影响页面。
 */
export async function initHostBridge(): Promise<boolean> {
  if (typeof window === 'undefined') return false

  const cef = window.CefSharp
  if (!cef || typeof cef.BindObjectAsync !== 'function') {
    hostBridge.ready = false
    return false
  }

  try {
    await cef.BindObjectAsync('hostApi')
    hostBridge.ready = typeof window.hostApi === 'object' && window.hostApi !== null
  } catch (error) {
    hostBridge.ready = false
    hostBridge.error = String(error)
    console.error('[bridge] CefSharp.BindObjectAsync 失败', error)
  }

  return hostBridge.ready
}

async function invoke<T>(call: (api: CefHostApi) => Promise<T>, fallback: T): Promise<T> {
  const bridge = typeof window === 'undefined' ? undefined : window.hostApi
  if (!bridge) return fallback
  try {
    return await call(bridge)
  } catch (error) {
    console.error('[bridge] 调用宿主失败', error)
    return fallback
  }
}

export const host = {
  /** 读取宿主/内核版本等运行环境信息 */
  async info(): Promise<HostInfo> {
    const raw = await invoke<string | null>((api) => api.getHostInfo(), null)
    if (!raw) return MOCK_INFO
    try {
      return { ...MOCK_INFO, ...(JSON.parse(raw) as Partial<HostInfo>) }
    } catch (error) {
      console.error('[bridge] 解析宿主信息失败', error)
      return MOCK_INFO
    }
  },

  /** 往返调用演示：前端 -> C# -> 前端 */
  echo(text: string): Promise<string> {
    return invoke((api) => api.echo(text), `[浏览器预览] ${text}`)
  },

  minimize(): Promise<void> {
    return invoke((api) => api.minimize(), undefined)
  },

  toggleMaximize(): Promise<void> {
    return invoke((api) => api.toggleMaximize(), undefined)
  },

  closeWindow(): Promise<void> {
    return invoke((api) => api.closeWindow(), undefined)
  },

  openDevTools(): Promise<void> {
    return invoke((api) => api.openDevTools(), undefined)
  },

  setZoomLevel(level: number): Promise<void> {
    return invoke((api) => api.setZoomLevel(level), undefined)
  },

  openUserDataFolder(): Promise<void> {
    return invoke((api) => api.openUserDataFolder(), undefined)
  },

  openLogFolder(): Promise<void> { return invoke((api) => api.openLogFolder(), undefined) },
  recordPlaybackLog(event: string, data: Record<string, unknown>, level = 'info'): Promise<void> {
    return invoke((api) => api.recordPlaybackLog(JSON.stringify({ event, data, level })), undefined)
  },

  /**
   * 让宿主代理一个媒体地址，返回 `stream://media/<id>`。
   * 浏览器预览模式没有宿主，直接返回原地址（浏览器里 http 本来就能播）。
   */
  registerStream(url: string, headers: Record<string, string> = {}): Promise<string> {
    const headersJson = JSON.stringify(headers)
    return invoke((api) => api.registerStream(url, headersJson), url)
  },

  getStreamError(url: string): Promise<string> {
    return invoke((api) => api.getStreamError(url), '')
  },

  async getStreamFailure(url: string): Promise<{ message: string; recoverable: boolean }> {
    const raw = await invoke((api) => api.getStreamFailure(url), '')
    return raw ? JSON.parse(raw) : { message: '', recoverable: false }
  },

  async recoverStream(url: string): Promise<StreamRecoveryResult> {
    const types = JSON.stringify(supportedAudioTypes())
    const raw = await invoke((api) => api.recoverStream(url, types), '')
    return raw ? JSON.parse(raw) : { recoverable: true }
  },

  /**
   * 让宿主解析 B站 音轨，返回直接代理或实时转码的 stream:// 地址。
   * 浏览器预览模式没有宿主，直接返回失败。
   */
  async resolveBilibili(url: string): Promise<BilibiliResult> {
    return this.resolveWith('resolveBilibili', url, '浏览器预览模式没有宿主，无法解析 B站')
  },

  /** 解析 YouTube：yt-dlp 只取流地址，不下载媒体文件 */
  async resolveYouTube(url: string): Promise<BilibiliResult> {
    return this.resolveWith('resolveYouTube', url, '浏览器预览模式没有宿主，无法解析 YouTube')
  },

  /** B站 / YouTube 共用：调宿主 -> 解析 JSON -> 失败时给降级对象 */
  async resolveWith(
    method: 'resolveBilibili' | 'resolveYouTube',
    url: string,
    fallbackMessage: string,
  ): Promise<BilibiliResult> {
    const supported = JSON.stringify(supportedAudioTypes())
    const raw = await invoke<string | null>((api) => api[method](url, supported), null)
    if (!raw) {
      return {
        ok: false,
        bvid: null,
        title: null,
        artist: null,
        duration: 0,
        streamUrl: null,
        cached: false,
        error: fallbackMessage,
      }
    }

    try {
      return JSON.parse(raw) as BilibiliResult
    } catch (error) {
      console.error('[bridge] 解析结果失败', error)
      return {
        ok: false,
        bvid: null,
        title: null,
        artist: null,
        duration: 0,
        streamUrl: null,
        cached: false,
        error: String(error),
      }
    }
  },

  /**
   * 搜 B站 视频：关键词由调用方把歌名 / 作者 / 专辑拼好。
   * 浏览器预览模式没有宿主，返回失败结果（UI 会退化成给搜索页链接）。
   */
  async searchBilibili(keyword: string, limit = 6): Promise<MediaSearchResult> {
    return this.searchWith('searchBilibili', 'bilibili', keyword, limit, '浏览器预览模式没有宿主，无法搜索 B站')
  },

  /** 搜 YouTube 视频（宿主用 yt-dlp 的 ytsearchN，带系统代理） */
  async searchYouTube(keyword: string, limit = 6): Promise<MediaSearchResult> {
    return this.searchWith(
      'searchYouTube',
      'youtube',
      keyword,
      limit,
      '浏览器预览模式没有宿主，无法搜索 YouTube',
    )
  },

  /** 两个平台共用：调宿主 -> 解析 JSON -> 失败时给降级对象 */
  async searchWith(
    method: 'searchBilibili' | 'searchYouTube',
    source: MediaSearchSource,
    keyword: string,
    limit: number,
    fallbackMessage: string,
  ): Promise<MediaSearchResult> {
    const empty: MediaSearchResult = { ok: false, source, keyword, items: [], error: fallbackMessage }
    const raw = await invoke<string | null>((api) => api[method](keyword, limit), null)
    if (!raw) return empty

    try {
      const parsed = JSON.parse(raw) as MediaSearchResult
      return {
        ...parsed,
        source,
        keyword,
        items: parsed.items ?? [],
        error: parsed.error ?? null,
      }
    } catch (error) {
      console.error('[bridge] 解析搜索结果失败', error)
      return { ...empty, error: String(error) }
    }
  },

  /** 用系统默认浏览器打开视频页；浏览器预览模式下退化成 window.open */
  openExternalUrl(url: string): Promise<void> {
    const bridge = typeof window === 'undefined' ? undefined : window.hostApi
    if (bridge) return invoke((api) => api.openExternalUrl(url), undefined)
    if (typeof window !== 'undefined') window.open(url, '_blank', 'noopener,noreferrer')
    return Promise.resolve()
  },
}
