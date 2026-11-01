import type { MediaSearchItem } from '../bridge/host'
// 这个文件同时被 node --test 直接加载（类型擦除），运行时的跨模块 import 必须带扩展名
import { SOURCE_INFOS, sourceLabels, type SourceInfo, type VideoLinkSource } from './sources.ts'

/**
 * 视频页链接识别与信息读取（纯逻辑 + 注入的站点表）。
 *
 * 站点判定来自 search/sources.ts 的 matchId，不再写死 bilibili / youtube；
 * 读取标题封面走站点的 info()（由 hostSources.ts 绑定到宿主）。
 */

/** 从视频页链接构造占位条目；识别不了直接抛错（错误文案里带上支持的站点） */
export function videoItemFromLink(input: string, sources: readonly SourceInfo[] = SOURCE_INFOS): MediaSearchItem {
  let url: URL
  try {
    url = new URL(input.trim())
  } catch {
    throw new Error(`请粘贴完整的 ${sourceLabels(sources)} 视频链接`)
  }
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('请使用 http 或 https 视频链接')

  const source = sources.find((candidate) => candidate.matchId(url) !== null)
  const id = source?.matchId(url)
  if (!source || !id) {
    const hints = sources
      .map((candidate) => candidate.linkHint)
      .filter((hint): hint is string => Boolean(hint))
    throw new Error(`请输入 ${sourceLabels(sources)} 视频页链接${hints.length ? `；${hints.join('；')}` : ''}`)
  }

  // 保留分 P、播放列表和时间等参数；视频 ID 用于列表去重。
  return {
    source: source.key,
    id,
    url: url.href,
    title: source.placeholderTitle(id),
    author: '',
    thumbnail: '',
    duration: 0,
    playCount: 0,
  }
}

/** 识别链接 + 读取真实标题/封面/作者；失败时抛错，不把占位标题当成成功结果 */
export async function resolveVideoLink(
  input: string,
  sources: readonly VideoLinkSource[],
): Promise<MediaSearchItem> {
  const item = videoItemFromLink(input, sources)
  const source = sources.find((candidate) => candidate.key === item.source)
  if (!source) throw new Error(`暂不支持的站点：${item.source}`)

  const result = await source.info(item.url)
  if (!result.ok || !result.item) {
    throw new Error(result.error || `未能读取 ${source.kindLabel} 视频信息，请重试`)
  }
  return { ...result.item, source: item.source, id: item.id, url: item.url }
}
