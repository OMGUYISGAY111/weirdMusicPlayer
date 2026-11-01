/**
 * 「支持哪些远端站点」的唯一真源：纯常量 + 类型守卫，没有任何 import。
 *
 * 放在最底层，是因为三处都要用它，而它们都不想互相依赖：
 *   - bridge/host.ts 的类型别名（MediaSearchSource）
 *   - 播放列表存档校验（不认识的 kind 直接丢弃，而不是当成 YouTube）
 *   - search/sources.ts 的站点描述表
 *
 * 加一个站点：先往 REMOTE_SOURCE_KEYS 里加键（类型、持久化校验会自动跟上），
 * 再在 search/sources.ts 补一条描述，最后在宿主 HostApi 里补该站点的解析。
 */

export const REMOTE_SOURCE_KEYS = ['bilibili', 'youtube'] as const

/** 远端站点（视频页 → 音轨由宿主解析） */
export type RemoteSourceKey = (typeof REMOTE_SOURCE_KEYS)[number]

export function isRemoteSourceKey(value: unknown): value is RemoteSourceKey {
  return typeof value === 'string' && (REMOTE_SOURCE_KEYS as readonly string[]).includes(value)
}

/** 宿主返回的字符串 source 对齐到已知站点；不是已知站点返回 null */
export function normalizeSourceKey(value: string | null | undefined): RemoteSourceKey | null {
  return isRemoteSourceKey(value) ? value : null
}
