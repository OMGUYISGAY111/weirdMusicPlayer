import type { RemoteSourceKey } from './sourceKeys'

/**
 * 搜索模式：入口（搜索栏）只认识模式，结果面板只认模式声明的站点集合。
 *
 * 「这个模式涉及哪些网站」写在这里，而不是散落在组件里，
 * 于是以后加一个「只搜 B站」或「搜播客」的模式时，执行和面板都不用改。
 */

export type SearchModeKey = 'song' | 'link'

export interface SearchMode {
  key: SearchModeKey
  /** 搜索栏里的模式标题 */
  label: string
  /** 这个模式涉及哪些站点；顺序即结果面板顺序 */
  sources: readonly RemoteSourceKey[]
}

export const SEARCH_MODES: readonly SearchMode[] = [
  {
    key: 'song',
    label: '歌曲名 · 歌手 · 专辑',
    sources: ['bilibili', 'youtube'],
  },
  {
    key: 'link',
    label: 'B站 / YouTube 链接',
    sources: ['bilibili', 'youtube'],
  },
]

export function findSearchMode(key: SearchModeKey): SearchMode {
  return SEARCH_MODES.find((mode) => mode.key === key) ?? SEARCH_MODES[0]!
}

/** 模式涉及的站点键，顺序即面板顺序 */
export function modeSourceKeys(key: SearchModeKey): readonly RemoteSourceKey[] {
  return findSearchMode(key).sources
}
