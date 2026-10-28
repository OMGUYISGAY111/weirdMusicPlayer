import type { MediaSearchResult, MediaSearchSource } from '../bridge/host'

export interface SongVideoQuery {
  song: string
  artist?: string
  album?: string
}

export type VideoSearchProviders = Record<
  MediaSearchSource,
  (keyword: string, limit: number) => Promise<MediaSearchResult>
>

export function songKeyword(query: SongVideoQuery): string {
  return [query.song, query.artist, query.album]
    .map((value) => (value ?? '').trim())
    .filter(Boolean)
    .join(' ')
}

/** 两个平台分别通知结果；单个平台失败不会丢失另一个平台的链接。 */
export async function searchSongVideos(
  query: SongVideoQuery,
  providers: VideoSearchProviders,
  limit = 6,
  onResult?: (result: MediaSearchResult) => void,
): Promise<MediaSearchResult[]> {
  if (!query.song.trim()) throw new Error('请先填写歌曲名')
  const keyword = songKeyword(query)
  const count = Number.isFinite(limit) ? Math.max(1, Math.min(30, Math.floor(limit))) : 6
  return Promise.all((['bilibili', 'youtube'] as const).map(async (source) => {
    let result: MediaSearchResult
    try {
      const response = await providers[source](keyword, count)
      result = { ...response, source, keyword }
      if (!result.ok && !result.error) result.error = '搜索失败，请稍后重试'
    } catch (error) {
      result = {
        ok: false, source, keyword, items: [],
        error: error instanceof Error ? error.message : String(error),
      }
    }
    onResult?.(result)
    return result
  }))
}
