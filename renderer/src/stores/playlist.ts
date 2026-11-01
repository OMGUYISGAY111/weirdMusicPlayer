import { computed, onScopeDispose, ref, shallowRef } from 'vue'
import { defineStore } from 'pinia'
import { createPlaylist } from '../playlist/playlist'
import type { PlaylistTrack } from '../playlist/playlist'
import { resolveTrack } from '../playlist/hostAdapter'
import { connectPlaylistPlayback } from '../playlist/playbackAdapter'
import type { PlaybackController } from '../playback/types'
import { createPlaylistLibraryPersistence, libraryFileText } from '../playlist/persistence'
import {
  activeRecord,
  createPlaylistId,
  createRecord,
  mergeLibrary,
  normalizeName,
  parseLibraryText,
  serializeLibrary,
  toPlaylistData,
  uniqueName,
  type PlaylistLibrary,
} from '../playlist/library'
import { host } from '../bridge/host'

/** 加进列表的结果：界面用它提示「已加入 XX」或「XX 里已有」 */
export interface AddToPlaylistResult {
  playlistId: string
  playlistName: string
  /** 列表里原来就有这首（只更新了标题 / 作者 / 封面） */
  existing: boolean
}

/** 导入 / 导出的结果；message 为空表示用户取消，界面不必提示 */
export interface PlaylistTransferResult {
  ok: boolean
  message: string
}

/**
 * 多播放列表状态仓库。
 *
 * 两个概念**互不影响**：
 *   - 选中列表（activeId）：界面上正在浏览 / 编辑的列表，切换它只改选中项，不碰播放器；
 *   - 播放队列（playerListId）：播放核心里现在装着的那份列表，只有从列表里点「播放」
 *     （或搜索页「立即播放」）才会切换 —— 切歌时先停掉当前音频，再把目标列表灌进核心。
 *
 * 实现上只有一个播放核心 + 一份列表库：
 *   - 核心状态（条目 / 模式 / 当前曲目 / 历史）只写回 **playerListId** 对应的记录，
 *     所以浏览别的列表不会把 A 的歌写进 B；
 *   - 对播放中列表的增删改走核心（保证正在播的队列一致），对其它列表的增删改直接改记录；
 *   - 删除正在播放的列表会停掉播放，并把播放队列落到剩下的列表上。
 */
export const usePlaylistStore = defineStore('playlist', () => {
  const storageError = shallowRef('')
  const persistence = createPlaylistLibraryPersistence(
    () => window.localStorage,
    (message) => { storageError.value = message },
  )

  const library = shallowRef<PlaylistLibrary>(persistence.load())
  const playerListId = ref(library.value.activeId)
  const queue = createPlaylist(resolveTrack, { initial: toPlaylistData(activeRecord(library.value)) })
  const state = shallowRef(queue.snapshot())

  let lastSaved = serializeLibrary(library.value)

  const playlists = computed(() => library.value.playlists)
  const activeId = computed(() => library.value.activeId)
  /** 选中（正在浏览）的列表 */
  const active = computed(() => activeRecord(library.value))
  /** 播放器里现在装着的列表 */
  const player = computed(() => library.value.playlists.find((record) => record.id === playerListId.value) ?? activeRecord(library.value))
  const playerName = computed(() => player.value.name)
  /** 当前列表的曲目：播放中的列表用核心实时数据，其它列表用存档数据 */
  const activeItems = computed(() => activeId.value === playerListId.value ? state.value.items : active.value.items)
  /** 只有选中列表正好是播放队列时，才标出「当前曲目」 */
  const activeCurrentId = computed(() => activeId.value === playerListId.value ? state.value.currentId : null)
  const currentTrack = computed(() => state.value.items.find((item) => item.id === state.value.currentId))

  /** 把核心的实时状态并回**播放队列**对应的记录（不落盘） */
  function snapshotLibrary(): PlaylistLibrary {
    const snapshot = state.value
    return {
      ...library.value,
      playlists: library.value.playlists.map((record) => record.id === playerListId.value
        ? {
            ...record,
            items: snapshot.items,
            mode: snapshot.mode,
            currentId: snapshot.currentId,
            history: snapshot.history,
          }
        : record),
    }
  }

  /** 写盘：内容没变就跳过，避免暂停 / 缓冲这类状态变化反复写 localStorage */
  function persist(next: PlaylistLibrary): void {
    library.value = next
    const text = serializeLibrary(next)
    if (text === lastSaved) return
    lastSaved = text
    persistence.save(next)
  }

  onScopeDispose(queue.subscribe((value) => {
    state.value = value
    persist(snapshotLibrary())
  }))
  onScopeDispose(() => queue.dispose())

  // ── 列表管理 ────────────────────────────────────────────────────────

  /** 切换正在浏览的列表：只改选中项，播放器完全不动 */
  function activate(id: string): void {
    if (id === library.value.activeId) return
    if (!library.value.playlists.some((record) => record.id === id)) return
    persist({ ...snapshotLibrary(), activeId: id })
  }

  /** 新建列表并选中它（不影响正在播放的列表） */
  function create(name?: string): string {
    const record = createRecord(
      createPlaylistId(),
      uniqueName(playlists.value.map((entry) => entry.name), name ?? '新建列表'),
    )
    persist({ ...snapshotLibrary(), playlists: [...library.value.playlists, record], activeId: record.id })
    return record.id
  }

  function rename(id: string, name: string): void {
    const clean = normalizeName(name, '')
    if (!clean) return
    const others = playlists.value.filter((record) => record.id !== id).map((record) => record.name)
    const finalName = uniqueName(others, clean)
    const current = snapshotLibrary()
    persist({
      ...current,
      playlists: current.playlists.map((record) => record.id === id ? { ...record, name: finalName } : record),
    })
  }

  /** 删除列表：只剩一个时不允许删；删掉正在播放的列表会停掉播放 */
  function removePlaylist(id: string): void {
    const removingPlayer = id === playerListId.value
    if (library.value.playlists.length <= 1) return
    if (!library.value.playlists.some((record) => record.id === id)) return

    const current = snapshotLibrary()
    const remaining = current.playlists.filter((record) => record.id !== id)
    const nextActiveId = current.activeId === id ? remaining[0]!.id : current.activeId

    if (!removingPlayer) {
      persist({ ...current, playlists: remaining, activeId: nextActiveId })
      return
    }

    // 播放队列被删：停掉播放，队列落到（新的）选中列表上
    const nextPlayer = remaining.find((record) => record.id === nextActiveId) ?? remaining[0]!
    library.value = { ...current, playlists: remaining, activeId: nextActiveId }
    playerListId.value = nextPlayer.id
    queue.replace(toPlaylistData(nextPlayer))
  }

  // ── 播放 ────────────────────────────────────────────────────────────

  /** 播放器自己的继续 / 重播（底部播放条用），不切换列表 */
  function play(id?: string): Promise<void> {
    return queue.play(id)
  }

  /**
   * 从某个列表开始播放：列表不是当前播放队列时，先把旧队列状态并回库，
   * 再把目标列表灌进核心（会停掉当前音频），最后播放指定曲目。
   * 这是唯一会改变播放器状态的列表操作。
   */
  async function playFrom(listId: string, trackId?: string): Promise<void> {
    const target = library.value.playlists.find((record) => record.id === listId)
    if (!target) throw new Error('播放列表不存在')

    if (listId === playerListId.value) {
      if (listId !== library.value.activeId) persist({ ...snapshotLibrary(), activeId: listId })
      await queue.play(trackId)
      return
    }

    const current = snapshotLibrary()
    library.value = { ...current, activeId: listId }
    playerListId.value = listId
    queue.replace(toPlaylistData(target))
    await queue.play(trackId)
  }

  /**
   * 连接播放端。播放核心只有一个（换列表只是换内容），
   * 所以整个应用只需要连接一次，不必随列表切换重连。
   */
  function connectPlayer(controller: PlaybackController): () => void {
    return connectPlaylistPlayback(queue, controller)
  }

  // ── 条目 ────────────────────────────────────────────────────────────

  /**
   * 加进指定列表。playlistId 为 null 表示「新建一个列表并加入」，
   * 新建的列表不会抢走当前选中，也不会停掉正在播放的歌。
   */
  function addTo(playlistId: string | null, track: PlaylistTrack): AddToPlaylistResult {
    if (playlistId === null) {
      const record = createRecord(
        createPlaylistId(),
        uniqueName(playlists.value.map((entry) => entry.name), '新建列表'),
        { items: [track] },
      )
      persist({ ...snapshotLibrary(), playlists: [...library.value.playlists, record] })
      return { playlistId: record.id, playlistName: record.name, existing: false }
    }

    if (playlistId !== playerListId.value) {
      // 不是播放队列：直接改记录，不碰播放器
      const target = library.value.playlists.find((record) => record.id === playlistId)
      if (!target) throw new Error('目标播放列表不存在')
      const existing = target.items.some((item) => item.id === track.id)
      const items = existing
        ? target.items.map((item) => item.id === track.id ? mergeMetadata(item, track) : item)
        : [...target.items, track]
      const current = snapshotLibrary()
      persist({
        ...current,
        playlists: current.playlists.map((record) => record.id === playlistId ? { ...record, items } : record),
      })
      return { playlistId, playlistName: target.name, existing }
    }

    // 播放队列：走核心，核心状态变化会同步回库
    const existing = state.value.items.some((item) => item.id === track.id)
    const id = queue.add(track)
    if (existing) queue.updateMetadata(id, track)
    return { playlistId, playlistName: player.value.name, existing }
  }

  /** 从当前选中的列表移除一首：播放队列走核心，其它列表只改记录 */
  function removeTrack(trackId: string): void {
    if (library.value.activeId === playerListId.value) {
      queue.remove(trackId)
      return
    }
    const record = active.value
    if (!record.items.some((item) => item.id === trackId)) return
    const current = snapshotLibrary()
    persist({
      ...current,
      playlists: current.playlists.map((entry) => entry.id === record.id
        ? {
            ...entry,
            items: entry.items.filter((item) => item.id !== trackId),
            history: entry.history.filter((id) => id !== trackId),
            currentId: entry.currentId === trackId ? null : entry.currentId,
          }
        : entry),
    })
  }

  /** 清空当前选中的列表：同上，只影响这份列表 */
  function clearActive(): void {
    if (library.value.activeId === playerListId.value) {
      queue.clear()
      return
    }
    const record = active.value
    const current = snapshotLibrary()
    persist({
      ...current,
      playlists: current.playlists.map((entry) => entry.id === record.id
        ? { ...entry, items: [], currentId: null, history: [] }
        : entry),
    })
  }

  // ── 导入 / 导出 ─────────────────────────────────────────────────────

  async function exportToFile(): Promise<PlaylistTransferResult> {
    const current = snapshotLibrary()
    const result = await host.exportPlaylists(libraryFileText(current))
    if (!result.ok) return { ok: false, message: result.error || '导出失败' }
    if (result.canceled) return { ok: false, message: '' }
    return { ok: true, message: `已导出 ${current.playlists.length} 个列表：${result.path}` }
  }

  async function importFromFile(): Promise<PlaylistTransferResult> {
    const result = await host.importPlaylists()
    if (!result.ok) return { ok: false, message: result.error || '导入失败' }
    if (result.canceled || !result.text) return { ok: false, message: '' }
    try {
      const imported = parseLibraryText(result.text)
      const merged = mergeLibrary(snapshotLibrary(), imported)
      persist(merged.library)
      return { ok: true, message: `已导入 ${merged.names.length} 个列表：${merged.names.join('、')}` }
    } catch (error) {
      return { ok: false, message: `导入失败：${error instanceof Error ? error.message : String(error)}` }
    }
  }

  return {
    // 状态
    state, currentTrack, storageError, playlists, activeId, active, library,
    activeItems, activeCurrentId, playerName, playerListId,
    // 列表管理（只影响选中项）
    activate, create, rename, removePlaylist,
    // 条目
    addTo, removeTrack, clearActive,
    // 播放：play 是播放器自己的操作，playFrom 才会切换播放队列
    play, playFrom, pause: queue.pause, resume: queue.resume, next: queue.next, previous: queue.previous,
    setMode: queue.setMode, canNext: queue.canNext, canPrevious: queue.canPrevious,
    connectPlayer,
    // 导入导出
    exportToFile, importFromFile,
  }
})

/** 同一首歌再次加入时，用新信息补全旧条目（不覆盖已有值） */
function mergeMetadata(item: PlaylistTrack, incoming: PlaylistTrack): PlaylistTrack {
  const next = {
    ...item,
    title: incoming.title || item.title,
    artist: item.artist || incoming.artist,
    cover: item.cover || incoming.cover,
  }
  return next.title === item.title && next.artist === item.artist && next.cover === item.cover ? item : next
}
