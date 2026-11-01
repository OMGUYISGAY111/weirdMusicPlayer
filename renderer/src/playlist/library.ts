import type { PlaybackMode, PlaylistData, PlaylistTrack } from './playlist'
import { isRemoteSourceKey } from '../search/sourceKeys.ts'

/**
 * 多播放列表的数据层（纯函数，无 Vue / 宿主 / 存储依赖）。
 *
 * - 一份「列表库」= 若干命名列表 + 当前选中的列表
 * - 每个列表记录条目、播放模式、当前曲目和历史；不含播放状态和临时流地址
 * - 存档 / 导入导出都走同一套校验：坏条目、重复条目、运行期地址（blob/stream/File）会被丢掉
 *
 * 这个文件同时被 node --test 直接加载（类型擦除），运行时的跨模块 import 必须带扩展名。
 */

export const LIBRARY_VERSION = 2
export const DEFAULT_PLAYLIST_NAME = '我的音乐'

export interface PlaylistRecord {
  id: string
  name: string
  items: readonly PlaylistTrack[]
  mode: PlaybackMode
  currentId: string | null
  history: readonly string[]
  /** 创建时间（毫秒） */
  createdAt: number
}

export interface PlaylistLibrary {
  playlists: readonly PlaylistRecord[]
  activeId: string
}

/** 界面用的轻量列表信息（「加入列表」时选择目标用） */
export interface PlaylistOption {
  id: string
  name: string
  count: number
  active: boolean
}

const MODES: readonly PlaybackMode[] = ['sequential', 'repeat-all', 'repeat-one', 'shuffle']
const DEFAULT_MODE: PlaybackMode = 'sequential'

export function isPlaybackMode(value: unknown): value is PlaybackMode {
  return typeof value === 'string' && (MODES as readonly string[]).includes(value)
}

/** 列表 id：浏览器和 Node 都有 crypto.randomUUID，缺失时退化成时间戳 */
export function createPlaylistId(): string {
  const uuid = globalThis.crypto?.randomUUID?.()
  if (uuid) return uuid
  return `pl-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}

export function normalizeName(name: unknown, fallback = DEFAULT_PLAYLIST_NAME): string {
  const text = typeof name === 'string' ? name.trim().replace(/\s+/g, ' ') : ''
  return text ? text.slice(0, 40) : fallback
}

/** 同名时追加「 2」「 3」…，不覆盖已有列表 */
export function uniqueName(existing: readonly string[], desired: string): string {
  const base = normalizeName(desired)
  const taken = new Set(existing.map((name) => name.trim()))
  if (!taken.has(base)) return base
  for (let index = 2; index < 1000; index += 1) {
    const candidate = `${base} ${index}`
    if (!taken.has(candidate)) return candidate
  }
  return `${base} ${Date.now().toString(36)}`
}

export function createRecord(
  id: string,
  name: string,
  options: {
    items?: readonly PlaylistTrack[]
    mode?: PlaybackMode
    currentId?: string | null
    history?: readonly string[]
    createdAt?: number
  } = {},
): PlaylistRecord {
  return {
    id,
    name: normalizeName(name),
    items: normalizeItems(options.items ?? []),
    mode: isPlaybackMode(options.mode) ? options.mode : DEFAULT_MODE,
    currentId: options.currentId ?? null,
    history: [...(options.history ?? [])],
    createdAt: options.createdAt ?? Date.now(),
  }
}

/** 空库：一个默认列表，直接可用 */
export function createLibrary(name: string = DEFAULT_PLAYLIST_NAME): PlaylistLibrary {
  const record = createRecord(createPlaylistId(), name)
  return { playlists: [record], activeId: record.id }
}

export function activeRecord(library: PlaylistLibrary): PlaylistRecord {
  return library.playlists.find((playlist) => playlist.id === library.activeId) ?? library.playlists[0]!
}

// ── 校验（存档、导入、恢复共用） ────────────────────────────────────────

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function stableUrl(value: unknown, protocols = ['http:', 'https:', 'app:', 'file:']): value is string {
  if (typeof value !== 'string' || !value.trim()) return false
  try { return protocols.includes(new URL(value).protocol) } catch { return false }
}

/** 单条歌曲：只保留可恢复的字段；运行期 File / blob / stream 地址直接丢弃 */
export function trackFromData(value: unknown): PlaylistTrack | null {
  if (!record(value) || typeof value.id !== 'string' || !value.id.trim() || typeof value.title !== 'string') return null
  const source = value.source
  if (!record(source)) return null
  const kind = source.kind
  if (kind !== 'audio' && !isRemoteSourceKey(kind)) return null
  if (source.file || !stableUrl(source.url, kind === 'audio' ? undefined : ['http:', 'https:'])) return null
  return {
    id: value.id,
    title: value.title,
    artist: typeof value.artist === 'string' ? value.artist : undefined,
    cover: stableUrl(value.cover) ? value.cover : undefined,
    source: { kind, url: source.url },
  }
}

/** 条目数组：丢弃坏条目和重复 id，顺序保留 */
export function normalizeItems(values: unknown): PlaylistTrack[] {
  const items: PlaylistTrack[] = []
  const ids = new Set<string>()
  for (const entry of Array.isArray(values) ? values : []) {
    const item = trackFromData(entry)
    if (!item || ids.has(item.id)) continue
    ids.add(item.id)
    items.push(item)
  }
  return items
}

/** 单条列表记录：修正模式、当前曲目和历史，丢弃指向不存在条目的引用 */
export function normalizeRecord(value: unknown, fallbackName: string, fallbackId: string): PlaylistRecord | null {
  if (!record(value)) return null
  const items = normalizeItems(value.items)
  const ids = new Set(items.map((item) => item.id))
  const id = typeof value.id === 'string' && value.id.trim() ? value.id : fallbackId
  const currentId = typeof value.currentId === 'string' && ids.has(value.currentId) ? value.currentId : null
  const history = Array.isArray(value.history)
    ? value.history.filter((entry): entry is string => typeof entry === 'string' && ids.has(entry)).slice(-100)
    : []
  // 随机播放要求历史末尾对应当前项
  if (currentId && history.at(-1) !== currentId) history.push(currentId)
  return {
    id,
    name: normalizeName(value.name, fallbackName),
    items,
    mode: isPlaybackMode(value.mode) ? value.mode : DEFAULT_MODE,
    currentId,
    history: history.slice(-100),
    createdAt: typeof value.createdAt === 'number' && Number.isFinite(value.createdAt) ? value.createdAt : Date.now(),
  }
}

/** 列表库：没有可用列表时补一个默认列表；activeId 必须指向存在的列表 */
export function normalizeLibrary(value: unknown): PlaylistLibrary | null {
  if (!record(value) || !Array.isArray(value.playlists)) return null
  const playlists: PlaylistRecord[] = []
  const usedIds = new Set<string>()
  const usedNames: string[] = []
  for (const entry of value.playlists) {
    const candidate = normalizeRecord(entry, '未命名列表', createPlaylistId())
    if (!candidate) continue
    const id = usedIds.has(candidate.id) ? createPlaylistId() : candidate.id
    usedIds.add(id)
    const name = uniqueName(usedNames, candidate.name)
    usedNames.push(name)
    playlists.push({ ...candidate, id, name })
  }
  if (!playlists.length) return createLibrary()
  const activeId = typeof value.activeId === 'string' && usedIds.has(value.activeId)
    ? value.activeId
    : playlists[0]!.id
  return { playlists, activeId }
}

// ── 存档 / 导入导出 ───────────────────────────────────────────────────

export function serializeLibrary(library: PlaylistLibrary): string {
  // 保存前再校验一遍：运行期地址（File / blob / stream）不能写进存档
  const playlists = library.playlists.map((playlist) => {
    const normalized = normalizeRecord(playlist, DEFAULT_PLAYLIST_NAME, playlist.id)!
    return {
      id: playlist.id,
      name: playlist.name,
      items: normalized.items,
      mode: normalized.mode,
      currentId: normalized.currentId,
      history: normalized.history,
      createdAt: playlist.createdAt,
    }
  })
  const activeId = playlists.some((playlist) => playlist.id === library.activeId)
    ? library.activeId
    : playlists[0]?.id ?? ''
  return JSON.stringify({ version: LIBRARY_VERSION, activeId, playlists })
}

/**
 * 解析存档文本。兼容三种来源：
 *   - 本程序导出的整库（version: 2 + playlists）
 *   - 旧版单列表存档（version: 1 + items）
 *   - 只有 items 的裸列表对象
 */
export function parseLibraryText(text: string): PlaylistLibrary {
  const value: unknown = JSON.parse(text)
  if (!record(value)) throw new Error('播放列表文件内容不是对象')

  if (Array.isArray(value.playlists)) {
    if (!value.playlists.length) throw new Error('播放列表文件里没有列表')
    const library = normalizeLibrary(value)
    if (!library) throw new Error('播放列表文件格式不受支持')
    return library
  }

  if (Array.isArray(value.items)) {
    const name = typeof value.name === 'string' && value.name.trim() ? value.name : '导入的列表'
    const item = normalizeRecord({ ...value, id: createPlaylistId(), name }, name, createPlaylistId())
    if (!item) throw new Error('播放列表文件里没有可用歌曲')
    return { playlists: [item], activeId: item.id }
  }

  throw new Error('播放列表文件格式不受支持')
}

/** 把导入的列表并进当前库：新 id、同名自动加序号，返回合并结果 */
export function mergeLibrary(
  target: PlaylistLibrary,
  imported: PlaylistLibrary,
): { library: PlaylistLibrary; names: string[] } {
  const playlists = [...target.playlists]
  const names = playlists.map((playlist) => playlist.name)
  const addedNames: string[] = []
  for (const entry of imported.playlists) {
    const name = uniqueName(names, entry.name)
    names.push(name)
    addedNames.push(name)
    playlists.push({ ...entry, id: createPlaylistId(), name, createdAt: Date.now() })
  }
  const activeId = playlists.some((playlist) => playlist.id === target.activeId)
    ? target.activeId
    : playlists[0]!.id
  return { library: { playlists, activeId }, names: addedNames }
}

/** 单条列表转换成播放核心认识的数据结构 */
export function toPlaylistData(record: PlaylistRecord): PlaylistData {
  return { items: record.items, currentId: record.currentId, mode: record.mode, history: record.history }
}
