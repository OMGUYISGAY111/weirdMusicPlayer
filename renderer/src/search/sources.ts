import type { MediaSearchItem, VideoInfoResult } from '../bridge/host'
import type { RemoteSourceKey } from './sourceKeys'

/**
 * 站点描述表（纯数据，不依赖宿主 / Vue / 网络）。
 *
 * 这里是「每个站点的差异在哪」的唯一真源：
 *   - 搜不到时该跳到哪个站内搜索页（searchUrl）
 *   - 视频页链接怎么认出本站视频 ID（matchId）
 *   - 面板标题、角标域名、播放列表里的平台名（label / site / kindLabel）
 *   - 站点专属的展示差异（formatMeta，例如播放量写法）
 *
 * 搜索、结果面板和播放链路都只认这张表；加站点见 sourceKeys.ts 里的说明。
 */

/** 站点描述：纯函数集合，不发起任何请求 */
export interface SourceInfo {
  key: RemoteSourceKey
  /** 结果面板标题 */
  label: string
  /** 面板右上角的域名角标 */
  site: string
  /** 播放列表里显示的平台名 */
  kindLabel: string
  /** 站内搜索页：宿主不可用或搜索失败时的兜底入口 */
  searchUrl: (keyword: string) => string
  /** 从视频页链接里取出本站视频 ID；不是本站链接返回 null */
  matchId: (url: URL) => string | null
  /** 只有 ID、还没读到真实标题时的占位标题 */
  placeholderTitle: (id: string) => string
  /** 链接识别失败时补充的站点提示（可选） */
  linkHint?: string
  /** 该站点专属的元信息文案；不提供则只显示作者 */
  formatMeta?: (item: MediaSearchItem) => string[]
}

/** 播放量写法：中文亿 / 万，两站目前一致，但通过 formatMeta 留成站点可覆盖的差异点 */
function formatPlayCount(count: number): string {
  if (!Number.isFinite(count) || count <= 0) return ''
  if (count >= 100_000_000) return `${(count / 100_000_000).toFixed(1)} 亿播放`
  if (count >= 10_000) return `${(count / 10_000).toFixed(1)} 万播放`
  return `${count} 播放`
}

function playCountMeta(item: MediaSearchItem): string[] {
  const text = formatPlayCount(item.playCount)
  return text ? [text] : []
}

const bilibili: SourceInfo = {
  key: 'bilibili',
  label: '哔哩哔哩',
  site: 'bilibili.com',
  kindLabel: 'B站',
  searchUrl: (keyword) => `https://search.bilibili.com/all?keyword=${encodeURIComponent(keyword)}`,
  matchId: (url) => {
    const hostname = url.hostname.toLowerCase()
    if (hostname !== 'bilibili.com' && !hostname.endsWith('.bilibili.com')) return null
    // 只认 BV 视频页；b23.tv 短链要先打开后复制完整地址
    return url.pathname.match(/^\/video\/(BV[a-zA-Z0-9]{10})\/?$/)?.[1] ?? null
  },
  placeholderTitle: (id) => `B站 视频 · ${id}`,
  linkHint: 'B站短链请先打开后复制完整视频页地址',
  formatMeta: playCountMeta,
}

const youtube: SourceInfo = {
  key: 'youtube',
  label: 'YouTube',
  site: 'youtube.com',
  kindLabel: 'YouTube',
  searchUrl: (keyword) => `https://www.youtube.com/results?search_query=${encodeURIComponent(keyword)}`,
  matchId: (url) => {
    const hostname = url.hostname.toLowerCase()
    const id = hostname === 'youtu.be'
      ? url.pathname.match(/^\/([\w-]{11})\/?$/)?.[1]
      : hostname === 'youtube.com' || hostname.endsWith('.youtube.com') || hostname === 'www.youtube-nocookie.com'
        ? url.pathname === '/watch'
          ? url.searchParams.get('v') ?? undefined
          : url.pathname.match(/^\/(?:shorts|live|embed)\/([\w-]{11})\/?$/)?.[1]
        : undefined
    return id && /^[\w-]{11}$/.test(id) ? id : null
  },
  placeholderTitle: (id) => `YouTube 视频 · ${id}`,
  formatMeta: playCountMeta,
}

/** 站点顺序即结果面板顺序 */
export const SOURCE_INFOS: readonly SourceInfo[] = [bilibili, youtube]

export function findSourceInfo(key: string | null | undefined): SourceInfo | undefined {
  return SOURCE_INFOS.find((source) => source.key === key)
}

/** 按 key 过滤站点描述，未知 key 忽略；顺序跟随传入的 key 顺序 */
export function sourceInfosFor(keys: readonly RemoteSourceKey[]): SourceInfo[] {
  return keys
    .map((key) => findSourceInfo(key))
    .filter((source): source is SourceInfo => source !== undefined)
}

/** 站点名串，用于错误提示，例如「B站 / YouTube」 */
export function sourceLabels(sources: readonly SourceInfo[]): string {
  return sources.map((source) => source.kindLabel).join(' / ')
}

/** 链接识别用到的站点能力：描述 + 读取视频信息 */
export interface VideoLinkSource extends SourceInfo {
  info: (url: string) => Promise<VideoInfoResult>
}
