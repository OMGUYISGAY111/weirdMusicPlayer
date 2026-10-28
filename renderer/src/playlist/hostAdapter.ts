import { host, type MediaSearchItem } from '../bridge/host'
import type { PlaylistTrack } from './playlist'
import type { PlaybackSource } from '../playback/types'

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
  const result = track.source.kind === 'bilibili'
    ? await host.resolveBilibili(track.source.url)
    : await host.resolveYouTube(track.source.url)
  if (!result.ok || !result.streamUrl) throw new Error(result.error || '视频音频解析失败')
  return { url: result.streamUrl }
}
