/**
 * UI 诊断模式：把「谁在什么时候把界面状态改了」记成时间线，落到宿主日志里。
 *
 * 什么时候用：某个交互只在别人的机器上出问题（点圆点、焦点、悬浮、快捷键这类输入/时序问题），
 * 自己这边却一切正常。让那人开一次诊断模式、点几下，把 `数据目录\logs\playback.jsonl` 发回来，
 * 就能看到完整的事件顺序，不用靠「你描述我猜」。
 *
 * 怎么开：
 *   - 宿主启动参数 `--debug-ui`（现场最省事：快捷方式后面加一个参数即可）；
 *   - 地址栏带 `?uitrace=1`（`pnpm dev:web` 浏览器预览时用）；
 *   - 运行时按 `Ctrl+Shift+D` 开/关，右下角会出现「诊断中」小标签提示对方可以点几下；
 *   - Console 里 `__uiTrace.on()` / `.off()` / `.dump()`。
 *
 * 记什么：全局 `pointerdown` / `click` / `focusin` / `focusout` / 关键按键（Tab、Esc、Enter、方向键、空格），
 * 以及组件主动上报的状态变更（例如 `search.controls.close` 带 reason）。每条带相对开启时刻的毫秒时间，
 * 另外单独记一条环境快照（DPR、视口、reduced-motion、宿主 DPI…）——「只有他那台机器出问题」时，
 * 这些字段往往就是答案。
 *
 * 注意：诊断开关生不生效都不影响交互；宿主没连上（浏览器预览）时 `recordPlaybackLog` 会走降级空实现，
 * 但 `__uiTrace.dump()` 仍然能看到缓冲里的事件。
 */
import { host, type HostInfo } from '@/bridge/host'

/** 缓冲上限：超过就丢最老的，避免长时间开启吃掉内存 */
const MAX_EVENTS = 200
/** 一次写盘最多带多少条，留出余量给宿主的 24KB 单条上限 */
const MAX_BATCH = 60
/** 攒批间隔：太短会把日志刷爆，太长则现场不够连续 */
const FLUSH_MS = 600

export interface UiTraceEvent {
  /** 相对开启时刻的毫秒数 */
  t: number
  name: string
  data?: Record<string, unknown>
}

let enabled = false
let installed = false
let startedAt = 0
let sequence = 0
let dropped = 0
/** 待写盘的事件：flush 时整批取走 */
let pending: UiTraceEvent[] = []
/** 最近的完整时间线：flush 不清空，`__uiTrace.dump()` 看的是这一份（浏览器预览下也能自证） */
let recent: UiTraceEvent[] = []
let flushTimer: number | undefined
let badge: HTMLElement | null = null
let hostInfo: HostInfo | null = null

function elapsed(): number {
  return Math.round(performance.now() - startedAt)
}

/** 把事件目标压成一个短描述（标签 + 最多 3 个类名），日志里够定位就够 */
export function describeTarget(target: EventTarget | null): string {
  if (!target) return 'null'
  if (target === window) return 'window'
  if (target === document) return 'document'
  if (target instanceof Element) {
    const classes = [...target.classList].slice(0, 3)
    return `${target.tagName.toLowerCase()}${classes.length ? `.${classes.join('.')}` : ''}`
  }
  if (target instanceof Node) return `#${target.nodeName.toLowerCase()}`
  return 'unknown'
}

function activeElementName(): string {
  return describeTarget(document.activeElement)
}

/** 组件里想进时间线的状态变更都走这里；没开诊断时是空操作。 */
export function traceUi(name: string, data?: Record<string, unknown>): void {
  if (!enabled) return
  sequence += 1
  const event: UiTraceEvent = { t: elapsed(), name, data }
  pending.push(event)
  recent.push(event)
  if (recent.length > MAX_EVENTS) {
    recent.shift()
    dropped += 1
  }
  if (pending.length > MAX_EVENTS) pending.shift()
  scheduleFlush()
}

export function isUiTraceEnabled(): boolean {
  return enabled
}

function scheduleFlush(): void {
  if (flushTimer !== undefined) return
  flushTimer = window.setTimeout(() => {
    flushTimer = undefined
    void flushUiTrace()
  }, FLUSH_MS)
}

/** 把缓冲里的事件写成一条 `ui_trace` 日志（宿主会按 JSONL 追加到 logs）。 */
export async function flushUiTrace(): Promise<void> {
  if (!pending.length) return
  const batch = pending.splice(0, MAX_BATCH)
  try {
    await host.recordPlaybackLog('ui_trace', { seq: sequence, dropped, events: batch })
  } catch {
    // 诊断永远不能影响交互
  }
  updateBadge()
  if (pending.length) scheduleFlush()
}

function environmentSnapshot(): Record<string, unknown> {
  const reducedMotion = typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
    : null
  return {
    href: location.href,
    userAgent: navigator.userAgent,
    language: navigator.language,
    dpr: window.devicePixelRatio,
    viewport: `${window.innerWidth}x${window.innerHeight}`,
    screen: `${window.screen.width}x${window.screen.height}`,
    reducedMotion,
    hasFocus: document.hasFocus(),
    host: hostInfo && {
      hostVersion: hostInfo.hostVersion,
      cefVersion: hostInfo.cefVersion,
      isDev: hostInfo.isDev,
      // 页面缩放 ≈ dpr ÷ (deviceDpi / 96)：远端机器「字怎么变小了」一目了然
      deviceDpi: hostInfo.deviceDpi,
      clientWidth: hostInfo.clientWidth,
      clientHeight: hostInfo.clientHeight,
    },
  }
}

function installListeners(): void {
  if (installed) return
  installed = true

  document.addEventListener('pointerdown', (event) => {
    if (!enabled) return
    traceUi('pointerdown', {
      target: describeTarget(event.target),
      x: Math.round(event.clientX),
      y: Math.round(event.clientY),
      button: event.button,
      pointerType: event.pointerType,
    })
  }, true)

  document.addEventListener('click', (event) => {
    if (!enabled) return
    traceUi('click', { target: describeTarget(event.target) })
  }, true)

  document.addEventListener('focusin', (event) => {
    if (!enabled) return
    traceUi('focusin', { target: describeTarget(event.target) })
  }, true)

  document.addEventListener('focusout', (event) => {
    if (!enabled) return
    const focus = event as FocusEvent
    traceUi('focusout', {
      target: describeTarget(focus.target),
      relatedTarget: describeTarget(focus.relatedTarget),
      active: activeElementName(),
    })
  }, true)

  document.addEventListener('keydown', (event) => {
    if (!enabled) return
    const keys = ['Tab', 'Escape', 'Enter', ' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']
    if (!keys.includes(event.key)) return
    traceUi('keydown', { key: event.key, target: describeTarget(event.target) })
  }, true)

  window.addEventListener('keydown', (event) => {
    if (!event.ctrlKey || !event.shiftKey) return
    if (event.key !== 'D' && event.key !== 'd') return
    event.preventDefault()
    setUiTraceEnabled(!enabled, 'shortcut')
  })
}

function showBadge(visible: boolean): void {
  if (typeof document === 'undefined') return
  if (!visible) {
    badge?.remove()
    badge = null
    return
  }
  if (badge) return
  badge = document.createElement('div')
  badge.dataset.uiTrace = '1'
  badge.style.cssText = [
    'position:fixed', 'right:14px', 'bottom:14px', 'z-index:2147483000',
    'padding:4px 10px', 'border-radius:999px', 'background:rgba(18,19,24,.82)',
    'color:#fff', 'font:12px/1.5 "Microsoft YaHei UI",system-ui,sans-serif',
    'box-shadow:0 6px 18px rgba(0,0,0,.25)', 'pointer-events:none', 'user-select:none',
  ].join(';')
  badge.textContent = '诊断中 · 点几下要复现的地方（Ctrl+Shift+D 关闭）'
  document.body.appendChild(badge)
}

function updateBadge(): void {
  if (!badge) return
  badge.textContent = `诊断中 · 已记录 ${sequence} 条（Ctrl+Shift+D 关闭）`
}

/** 开/关诊断。reason 会写进 `ui_trace_on` / `ui_trace_off`，方便对上是谁、为什么开的。 */
export function setUiTraceEnabled(next: boolean, reason: string): void {
  if (next === enabled) return
  enabled = next

  if (enabled) {
    startedAt = performance.now()
    sequence = 0
    dropped = 0
    pending = []
    recent = []
    installListeners()
    showBadge(true)
    console.info('[ui-trace] 诊断已开启', reason)
    void host.recordPlaybackLog('ui_trace_on', { reason, environment: environmentSnapshot() })
    return
  }

  if (flushTimer !== undefined) {
    window.clearTimeout(flushTimer)
    flushTimer = undefined
  }
  void flushUiTrace().then(() => host.recordPlaybackLog('ui_trace_off', { reason }))
  showBadge(false)
  console.info('[ui-trace] 诊断已关闭')
}

declare global {
  interface Window {
    /** 控制台手动开关：`__uiTrace.on()` / `.off()` / `.state()` / `.dump()` */
    __uiTrace?: {
      on: () => void
      off: () => void
      state: () => { enabled: boolean; pending: number; recent: number; dropped: number }
      dump: () => UiTraceEvent[]
      flush: () => Promise<void>
    }
  }
}

/**
 * 页面启动时调一次：监听器只装一次，是否记录由开关决定。
 * 宿主用 `--debug-ui` 启动时自动开启（信息来自 GetHostInfo）。
 */
export async function initUiTrace(): Promise<void> {
  installListeners()
  window.__uiTrace = {
    on: () => setUiTraceEnabled(true, 'console'),
    off: () => setUiTraceEnabled(false, 'console'),
    state: () => ({ enabled, pending: pending.length, recent: recent.length, dropped }),
    dump: () => recent.slice(),
    flush: flushUiTrace,
  }

  if (new URLSearchParams(location.search).get('uitrace') === '1') {
    setUiTraceEnabled(true, 'url:uitrace')
  }

  hostInfo = await host.info().catch(() => null)
  if (!enabled && hostInfo?.isDebugUi === true) {
    setUiTraceEnabled(true, 'host:--debug-ui')
  }
}
