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
}
