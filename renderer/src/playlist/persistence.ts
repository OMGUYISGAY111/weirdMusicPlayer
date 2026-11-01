import type { createPlaylist, PlaybackMode, PlaylistData, PlaylistTrack } from './playlist'
import { isRemoteSourceKey } from '../search/sourceKeys.ts'

export const PLAYLIST_STORAGE_KEY = 'music-player.playlist.v1'
type StorageAccess = () => Pick<Storage, 'getItem' | 'setItem'>

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function stableUrl(value: unknown, protocols = ['http:', 'https:', 'app:', 'file:']): value is string {
  if (typeof value !== 'string' || !value.trim()) return false
  try { return protocols.includes(new URL(value).protocol) } catch { return false }
}

function trackFromData(value: unknown): PlaylistTrack | null {
  if (!record(value) || typeof value.id !== 'string' || !value.id.trim() || typeof value.title !== 'string') return null
  const source = value.source
  if (!record(source)) return null
  const kind = source.kind
  // 已知站点由注册表决定；不认识的 kind 直接丢弃，绝不猜成某个站点
  if (kind !== 'audio' && !isRemoteSourceKey(kind)) return null
  // File、blob 和 stream 地址仅在当前会话有效，不能伪装成可恢复条目。
  if (source.file || !stableUrl(source.url, kind === 'audio' ? undefined : ['http:', 'https:'])) return null
  return {
    id: value.id,
    title: value.title,
    artist: typeof value.artist === 'string' ? value.artist : undefined,
    cover: stableUrl(value.cover) ? value.cover : undefined,
    source: { kind, url: source.url },
  }
}

function normalizedData(value: Record<string, unknown>): PlaylistData {
  const items: PlaylistTrack[] = []
  const ids = new Set<string>()
  for (const entry of Array.isArray(value.items) ? value.items : []) {
    const item = trackFromData(entry)
    if (!item || ids.has(item.id)) continue
    ids.add(item.id)
    items.push(item)
  }
  const modes: PlaybackMode[] = ['sequential', 'repeat-all', 'repeat-one', 'shuffle']
  const mode = modes.includes(value.mode as PlaybackMode) ? value.mode as PlaybackMode : 'sequential'
  const currentId = typeof value.currentId === 'string' && ids.has(value.currentId) ? value.currentId : null
  const history = Array.isArray(value.history)
    ? value.history.filter((id): id is string => typeof id === 'string' && ids.has(id)).slice(-100)
    : []
  // 随机模式要求历史末尾对应当前项。
  if (currentId && history.at(-1) !== currentId) history.push(currentId)
  return { items, currentId, mode, history: history.slice(-100) }
}

export function serializePlaylist(data: PlaylistData): string {
  return JSON.stringify({ version: 1, ...normalizedData({ ...data }) })
}

export function parsePlaylist(text: string): PlaylistData {
  const value: unknown = JSON.parse(text)
  if (!record(value) || value.version !== 1 || !Array.isArray(value.items)) throw new Error('播放列表存档格式不受支持')
  return normalizedData(value)
}

/** 存储适配器独立于核心。注入 localStorage 或其他同步存储均可。 */
export function createPlaylistPersistence(getStorage: StorageAccess, report: (message: string) => void = () => {}) {
  function load(): PlaylistData | undefined {
    try {
      const text = getStorage().getItem(PLAYLIST_STORAGE_KEY)
      return text === null ? undefined : parsePlaylist(text)
    } catch {
      report('无法读取已保存的播放列表，本次使用空列表。')
      return undefined
    }
  }

  function connect(queue: ReturnType<typeof createPlaylist>): () => void {
    let lastSaved = serializePlaylist(queue.snapshot())
    return queue.subscribe((state) => {
      const text = serializePlaylist(state)
      if (text === lastSaved) return
      try {
        getStorage().setItem(PLAYLIST_STORAGE_KEY, text)
        lastSaved = text
        report('')
      } catch {
        report('播放列表保存失败，本次变更可能无法在重启后恢复。')
      }
    })
  }
  return { load, connect }
}
