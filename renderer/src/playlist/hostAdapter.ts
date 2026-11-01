import type { MediaSearchItem } from '../bridge/host'
import type { PlaylistTrack } from './playlist'
import type { PlaybackSource } from '../playback/types'
import { findSearchSource } from '../search/hostSources'

/**
 * 搜索结果 / 播放列表条目 ↔ 宿主解析的适配层。
 * 站点解析走注册表（search/hostSources.ts），不再用 `=== 'bilibili' ? ... : ...`，
 * 因此新增站点不会静默落到 YouTube 分支上。
 */

export function trackFromSearch(item: MediaSearchItem): PlaylistTrack {
  return {
    id: `${item.source}:${item.id}`,
    title: item.title,
    artist: item.author,
    cover: item.thumbnail,
    source: { kind: item.source, url: item.url },
  }
}

export async function resolveTrack(track: PlaylistTrack): Promise<PlaybackSource> {
  if (track.source.kind === 'audio') return { file: track.source.file, url: track.source.url }
  const source = findSearchSource(track.source.kind)
  if (!source) throw new Error(`暂不支持的站点：${track.source.kind}`)
  const result = await source.resolve(track.source.url)
  if (!result.ok || !result.streamUrl) throw new Error(result.error || '视频音频解析失败')
  return { url: result.streamUrl }
}
