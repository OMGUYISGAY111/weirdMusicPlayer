import {
  host,
  type MediaResolveResult,
  type MediaSearchResult,
} from '../bridge/host'
import { SOURCE_INFOS, type SourceInfo, type VideoLinkSource } from './sources'
import { findSearchMode, type SearchModeKey } from './modes'

/**
 * 把纯数据站点描述（search/sources.ts）绑定到宿主调用。
 *
 * 组件只依赖这里的 SEARCH_SOURCES，不再认识 searchBilibili / getYouTubeVideoInfo
 * 这类「方法名 × 站点」的映射；宿主侧也只暴露 searchMedia / getVideoInfo / resolveMedia。
 */

export interface SearchSource extends VideoLinkSource {
  /** 关键词搜索（宿主侧走各平台自己的实现） */
  search: (keyword: string, limit: number) => Promise<MediaSearchResult>
  /** 播放前解析音轨，返回 stream:// 地址 */
  resolve: (url: string) => Promise<MediaResolveResult>
}

function bind(info: SourceInfo): SearchSource {
  return {
    ...info,
    search: (keyword, limit) => host.searchMedia(info.key, keyword, limit),
    info: (url) => host.getVideoInfo(info.key, url),
    resolve: (url) => host.resolveMedia(info.key, url),
  }
}

/** 所有站点，顺序即结果面板顺序 */
export const SEARCH_SOURCES: readonly SearchSource[] = SOURCE_INFOS.map(bind)

export function findSearchSource(key: string | null | undefined): SearchSource | undefined {
  return SEARCH_SOURCES.find((source) => source.key === key)
}

/** 某个模式涉及的站点（结果面板、链接识别都按它来） */
export function sourcesForMode(key: SearchModeKey): SearchSource[] {
  return findSearchMode(key).sources
    .map((sourceKey) => findSearchSource(sourceKey))
    .filter((source): source is SearchSource => source !== undefined)
}

/** 按视频页链接找到对应站点；都不匹配时返回 undefined */
export function sourceForLink(url: URL, sources: readonly SearchSource[] = SEARCH_SOURCES): SearchSource | undefined {
  return sources.find((source) => source.matchId(url) !== null)
}
