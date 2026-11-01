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
import type { RemoteSourceKey } from '../search/sourceKeys'

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
  /**
   * 按关键词搜索某个站点，返回 MediaSearchResult 的 JSON 字符串。
   * 站点用字符串标识（bilibili / youtube，见 search/sourceKeys.ts），
   * 前端只认站点键，不再维护「方法名 × 站点」的映射。
   */
  searchMedia(source: string, keyword: string, limit: number): Promise<string>
  /** 只读取某个站点的视频信息（标题/封面/作者），不解析音轨。 */
  getVideoInfo(source: string, url: string): Promise<string>
  /** 解析某个站点的音轨，返回可直接播放的 stream:// 地址。 */
  resolveMedia(source: string, url: string, supportedTypesJson: string): Promise<string>
  /** 用系统默认浏览器打开 http(s) 外链（视频页），避免把 CEF 窗口导航走 */
  openExternalUrl(url: string): Promise<void>
  /** 设置面板数据源：settings.json 里的值 + 环境变量回退 + 生效值 */
  getSettings(): Promise<string>
  /** 弹系统文件选择框挑 Cookie 文件，返回 { ok, path, error } */
  pickYouTubeCookiesFile(): Promise<string>
  /** 只体检某个路径能不能当 Cookie 文件用，不保存 */
  checkCookiesFile(path: string): Promise<string>
  /** 保存设置（JSON 字符串），返回 { ok, error, warning, settings } */
  saveSettings(json: string): Promise<string>
  /** 把整库播放列表写到用户选的文件，返回 { ok, canceled, path, error } */
  exportPlaylists(json: string): Promise<string>
  /** 选播放列表文件并读回文本，返回 { ok, canceled, path, fileName, text, error } */
  importPlaylists(): Promise<string>
}

/** 搜索来源：键表在 search/sourceKeys.ts（加站点只改那一处） */
export type MediaSearchSource = RemoteSourceKey

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

export interface VideoInfoResult {
  ok: boolean
  item: MediaSearchItem | null
  error: string | null
}

/** 设置面板里的一组成员：YouTube 登录方式。空字符串表示「没配」。 */
export interface CookieSelection {
  /** Netscape 格式 Cookie 文件路径 */
  youtubeCookiesFile: string
  /** 直接读取已登录浏览器的名称，语法同 yt-dlp --cookies-from-browser */
  youtubeCookiesBrowser: string
}

/** Cookie 文件体检结果（宿主扫文件头，不读取真实登录内容） */
export interface CookieFileStatus {
  path: string
  exists: boolean
  size: number
  /** 宿主格式化好的本地时间，空字符串表示未知 */
  modified: string
  /** 是否像 Netscape 格式 */
  netscape: boolean
  /** youtube.com / google.com 域的 Cookie 条数 */
  cookieCount: number
  expiredCount: number
  /** 给用户看的一句话说明 */
  note: string
}

/** 宿主 GetSettings() 返回的完整快照：设置文件里的值、环境变量回退、实际生效值 */
export interface SettingsSnapshot {
  ok: boolean
  /** 是否真的连着 C# 宿主（浏览器预览为 false） */
  inHost: boolean
  /** settings.json 的绝对路径，浏览器预览时为空 */
  settingsPath: string
  /** 设置文件里保存的值（设置面板的初值） */
  ui: CookieSelection
  /** 环境变量里另配的值（仅用于提示，设置优先） */
  environment: CookieSelection
  /** 实际生效的 YouTube 登录方式 */
  effective: {
    mode: 'file' | 'browser' | 'none'
    /** 生效值来自哪里：设置（settings.json）/ 环境变量 / 空字符串 */
    source: string
    youtubeCookiesFile: string
    youtubeCookiesBrowser: string
  }
  /** 当前生效 Cookie 文件的体检结果；没配文件时 path 为空 */
  cookieFile: CookieFileStatus
  /** 宿主的 yt-dlp 查找结果 */
  ytDlp: { found: boolean; command: string }
  /** settings.json 读不出来时的提示 */
  loadWarning: string
}

/** 保存设置的入参 */
export interface SettingsInput extends CookieSelection {
  /** 同时写入用户级环境变量，方便命令行工具复用 */
  persistToUserEnvironment: boolean
}

export interface SaveSettingsResult {
  ok: boolean
  error: string
  /** 保存成功但有需要注意的事（例如写环境变量失败） */
  warning: string
  /** 保存后的最新快照，失败时为 null */
  settings: SettingsSnapshot | null
}

/** 文件选择框的结果；取消时 ok 为 true、path 为空 */
export interface PickCookiesFileResult {
  ok: boolean
  path: string
  error: string
}

/** 导出播放列表的结果；canceled 为 true 表示用户取消了保存框 */
export interface PlaylistExportResult {
  ok: boolean
  canceled: boolean
  path: string
  error: string
}

/** 导入播放列表的结果；text 是文件原文，解析在前端完成 */
export interface PlaylistImportResult extends PlaylistExportResult {
  fileName: string
  text: string
}

/** 音轨解析结果（B站 / YouTube 等站点共用；bvid 字段沿用旧命名，放站点侧的视频 ID） */
export interface MediaResolveResult {
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

/** 空快照：浏览器预览（没有宿主）时设置面板照样能打开，只是不能保存 */
const MOCK_SETTINGS: SettingsSnapshot = {
  ok: true,
  inHost: false,
  settingsPath: '',
  ui: { youtubeCookiesFile: '', youtubeCookiesBrowser: '' },
  environment: { youtubeCookiesFile: '', youtubeCookiesBrowser: '' },
  effective: { mode: 'none', source: '', youtubeCookiesFile: '', youtubeCookiesBrowser: '' },
  cookieFile: {
    path: '',
    exists: false,
    size: 0,
    modified: '',
    netscape: false,
    cookieCount: 0,
    expiredCount: 0,
    note: '',
  },
  ytDlp: { found: false, command: '' },
  loadWarning: '',
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
   * 解析某个站点的音轨：宿主只取流地址，按 CEF 解码能力选择直接代理或实时转码。
   * 浏览器预览模式没有宿主，直接返回失败对象。
   */
  async resolveMedia(source: MediaSearchSource, url: string): Promise<MediaResolveResult> {
    const supported = JSON.stringify(supportedAudioTypes())
    const fallback: MediaResolveResult = {
      ok: false,
      bvid: null,
      title: null,
      artist: null,
      duration: 0,
      streamUrl: null,
      cached: false,
      error: '浏览器预览模式没有宿主，无法解析视频音频',
    }
    const raw = await invoke<string | null>((api) => api.resolveMedia(source, url, supported), null)
    if (!raw) return fallback
    try {
      return JSON.parse(raw) as MediaResolveResult
    } catch (error) {
      console.error('[bridge] 解析结果失败', error)
      return { ...fallback, error: String(error) }
    }
  },

  /**
   * 搜索某个站点：关键词由调用方拼好，站点由调用方决定。
   * 浏览器预览模式没有宿主，返回失败结果（UI 会退化成给站内搜索页链接）。
   */
  async searchMedia(source: MediaSearchSource, keyword: string, limit = 6): Promise<MediaSearchResult> {
    const empty: MediaSearchResult = { ok: false, source, keyword, items: [], error: '浏览器预览模式没有宿主，无法搜索' }
    const raw = await invoke<string | null>((api) => api.searchMedia(source, keyword, limit), null)
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

  /** 只读取某个站点的视频信息（标题/封面/作者），不解析音轨 */
  async getVideoInfo(source: MediaSearchSource, url: string): Promise<VideoInfoResult> {
    const empty: VideoInfoResult = { ok: false, item: null, error: '宿主未连接，无法读取视频信息' }
    const raw = await invoke<string | null>((api) => api.getVideoInfo(source, url), null)
    if (!raw) return empty
    try {
      return JSON.parse(raw) as VideoInfoResult
    } catch (error) {
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

  /** 读取设置快照；浏览器预览下返回空快照（inHost=false），设置面板据此禁用保存 */
  async getSettings(): Promise<SettingsSnapshot> {
    const raw = await invoke<string | null>((api) => api.getSettings(), null)
    if (!raw) return MOCK_SETTINGS
    try {
      const parsed = JSON.parse(raw) as SettingsSnapshot
      return { ...MOCK_SETTINGS, ...parsed, inHost: parsed.inHost !== false }
    } catch (error) {
      console.error('[bridge] 解析设置失败', error)
      return MOCK_SETTINGS
    }
  },

  /** 弹宿主的文件选择框；取消时 ok=true、path 为空 */
  async pickYouTubeCookiesFile(): Promise<PickCookiesFileResult> {
    const raw = await invoke<string | null>((api) => api.pickYouTubeCookiesFile(), null)
    if (!raw) return { ok: false, path: '', error: '浏览器预览模式没有宿主，无法打开文件选择框' }
    try {
      return JSON.parse(raw) as PickCookiesFileResult
    } catch (error) {
      return { ok: false, path: '', error: String(error) }
    }
  },

  /** 只体检路径（不保存），用户在设置面板里选完文件就能看到能不能用 */
  async checkCookiesFile(path: string): Promise<CookieFileStatus | null> {
    const raw = await invoke<string | null>((api) => api.checkCookiesFile(path), null)
    if (!raw) return null
    try {
      return (JSON.parse(raw) as { cookieFile: CookieFileStatus }).cookieFile ?? null
    } catch (error) {
      console.error('[bridge] 解析 Cookie 体检结果失败', error)
      return null
    }
  },

  /** 保存设置；宿主会校验路径并返回保存后的快照 */
  async saveSettings(input: SettingsInput): Promise<SaveSettingsResult> {
    const raw = await invoke<string | null>((api) => api.saveSettings(JSON.stringify(input)), null)
    if (!raw) return { ok: false, error: '浏览器预览模式没有宿主，无法保存设置', warning: '', settings: null }
    try {
      return JSON.parse(raw) as SaveSettingsResult
    } catch (error) {
      return { ok: false, error: String(error), warning: '', settings: null }
    }
  },

  /** 导出播放列表：宿子弹保存框并写文件 */
  async exportPlaylists(json: string): Promise<PlaylistExportResult> {
    const empty: PlaylistExportResult = { ok: false, canceled: false, path: '', error: '浏览器预览模式没有宿主，无法保存文件' }
    const raw = await invoke<string | null>((api) => api.exportPlaylists(json), null)
    if (!raw) return empty
    try {
      return JSON.parse(raw) as PlaylistExportResult
    } catch (error) {
      return { ...empty, error: String(error) }
    }
  },

  /** 导入播放列表：宿子弹文件框并读回原文，解析在前端 */
  async importPlaylists(): Promise<PlaylistImportResult> {
    const empty: PlaylistImportResult = {
      ok: false, canceled: false, path: '', fileName: '', text: '',
      error: '浏览器预览模式没有宿主，无法读取本地文件',
    }
    const raw = await invoke<string | null>((api) => api.importPlaylists(), null)
    if (!raw) return empty
    try {
      return JSON.parse(raw) as PlaylistImportResult
    } catch (error) {
      return { ...empty, error: String(error) }
    }
  },
}
