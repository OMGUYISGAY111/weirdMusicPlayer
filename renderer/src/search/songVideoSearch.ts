import type { MediaSearchResult } from '../bridge/host'
import type { RemoteSourceKey } from './sourceKeys'

/**
 * 按歌曲信息搜视频的纯逻辑：不依赖 Vue、宿主或具体站点。
 * 站点由调用方按「当前模式涉及的站点」传进来，谁传就搜谁。
 */

export interface SongVideoQuery {
  song: string
  artist?: string
  album?: string
}

/** 搜索执行需要的最小站点能力；SearchSource（hostSources.ts）天然满足 */
export interface SearchTarget {
  key: RemoteSourceKey
  search: (keyword: string, limit: number) => Promise<MediaSearchResult>
}

export function songKeyword(query: SongVideoQuery): string {
  return [query.song, query.artist, query.album]
    .map((value) => (value ?? '').trim())
    .filter(Boolean)
    .join(' ')
}

const MAX_LIMIT = 30
const DEFAULT_LIMIT = 6

/** 各站点并行执行；单个站点失败不影响其他站点，结果按传入顺序返回。 */
export async function searchSongVideos(
  query: SongVideoQuery,
  targets: readonly SearchTarget[],
  limit = DEFAULT_LIMIT,
  onResult?: (result: MediaSearchResult) => void,
): Promise<MediaSearchResult[]> {
  if (!query.song.trim()) throw new Error('请先填写歌曲名')
  const keyword = songKeyword(query)
  const count = Number.isFinite(limit)
    ? Math.max(1, Math.min(MAX_LIMIT, Math.floor(limit)))
    : DEFAULT_LIMIT

  return Promise.all(targets.map(async (target) => {
    let result: MediaSearchResult
    try {
      const response = await target.search(keyword, count)
      result = { ...response, source: target.key, keyword }
      if (!result.ok && !result.error) result.error = '搜索失败，请稍后重试'
    } catch (error) {
      result = {
        ok: false, source: target.key, keyword, items: [],
        error: error instanceof Error ? error.message : String(error),
      }
    }
    // 谁先回来先通知，界面不必等所有站点
    onResult?.(result)
    return result
  }))
}
