import type { PlaylistLibrary } from './library'
import {
  DEFAULT_PLAYLIST_NAME,
  LIBRARY_VERSION,
  createLibrary,
  createPlaylistId,
  normalizeRecord,
  parseLibraryText,
  serializeLibrary,
} from './library.ts'

/**
 * 列表库的存储适配器（localStorage）。
 * 只负责「读出来 / 写回去」，校验和格式都在 playlist/library.ts。
 *
 * 这个文件同时被 node --test 直接加载（类型擦除），运行时的跨模块 import 必须带扩展名。
 */

/** 多列表存档（v2） */
export const PLAYLIST_LIBRARY_STORAGE_KEY = 'music-player.playlists.v2'
/** 旧版单列表存档，只在升级时读取一次 */
export const LEGACY_PLAYLIST_STORAGE_KEY = 'music-player.playlist.v1'

type StorageAccess = () => Pick<Storage, 'getItem' | 'setItem'>

export interface PlaylistLibraryPersistence {
  load(): PlaylistLibrary
  save(library: PlaylistLibrary): void
}

/** 单列表存档（v1）→ 一个名为「我的音乐」的列表 */
function fromLegacyText(text: string): PlaylistLibrary | null {
  const record = normalizeRecord(JSON.parse(text), DEFAULT_PLAYLIST_NAME, createPlaylistId())
  if (!record) return null
  return { playlists: [{ ...record, name: DEFAULT_PLAYLIST_NAME }], activeId: record.id }
}

/** 存储适配器独立于核心；注入 localStorage 或其他同步存储均可。 */
export function createPlaylistLibraryPersistence(
  getStorage: StorageAccess,
  report: (message: string) => void = () => {},
): PlaylistLibraryPersistence {
  function load(): PlaylistLibrary {
    try {
      const storage = getStorage()
      const text = storage.getItem(PLAYLIST_LIBRARY_STORAGE_KEY)
      if (text !== null) return parseLibraryText(text)

      // 从旧版单列表升级：读成功顺手写成 v2，旧键保留（不销毁用户数据）
      const legacy = storage.getItem(LEGACY_PLAYLIST_STORAGE_KEY)
      if (legacy !== null) {
        const migrated = fromLegacyText(legacy)
        if (migrated) {
          try { storage.setItem(PLAYLIST_LIBRARY_STORAGE_KEY, serializeLibrary(migrated)) } catch { /* 写不进去就先用内存里的 */ }
          return migrated
        }
      }

      return createLibrary()
    } catch {
      report('无法读取已保存的播放列表，本次使用空列表。')
      return createLibrary()
    }
  }

  function save(library: PlaylistLibrary): void {
    try {
      getStorage().setItem(PLAYLIST_LIBRARY_STORAGE_KEY, serializeLibrary(library))
      report('')
    } catch {
      report('播放列表保存失败，本次变更可能无法在重启后恢复。')
    }
  }

  return { load, save }
}

/** 存档文件头：导出时带上版本号，导入时也接受旧版单列表 */
export function libraryFileText(library: PlaylistLibrary): string {
  const parsed = JSON.parse(serializeLibrary(library)) as Record<string, unknown>
  return JSON.stringify({ version: LIBRARY_VERSION, exportedAt: new Date().toISOString(), ...parsed }, null, 2)
}
