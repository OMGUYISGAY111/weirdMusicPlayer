import type { MediaSearchItem, MediaSearchSource, VideoInfoResult } from '../bridge/host'

export async function resolveVideoLink(
  input: string,
  providers: Record<MediaSearchSource, (url: string) => Promise<VideoInfoResult>>,
): Promise<MediaSearchItem> {
  const item = videoItemFromLink(input)
  const result = await providers[item.source](item.url)
  if (!result.ok || !result.item) throw new Error(result.error || `未能读取 ${item.source === 'bilibili' ? 'B站' : 'YouTube'} 视频信息，请重试`)
  return { ...result.item, source: item.source, id: item.id, url: item.url }
}

/** 将视频页链接转换为列表可使用的来源，音频仍在播放时由宿主解析。 */
export function videoItemFromLink(input: string): MediaSearchItem {
  let url: URL
  try { url = new URL(input.trim()) } catch { throw new Error('请粘贴完整的 B站或 YouTube 视频链接') }
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('请使用 http 或 https 视频链接')
  const hostname = url.hostname.toLowerCase()
  const bili = hostname === 'bilibili.com' || hostname.endsWith('.bilibili.com')
  const youtube = hostname === 'youtube.com' || hostname.endsWith('.youtube.com') || hostname === 'www.youtube-nocookie.com'
  const id = bili
    ? url.pathname.match(/^\/video\/(BV[a-zA-Z0-9]{10})\/?$/)?.[1]
    : hostname === 'youtu.be'
      ? url.pathname.match(/^\/([\w-]{11})\/?$/)?.[1]
      : youtube
        ? url.pathname === '/watch'
          ? url.searchParams.get('v') ?? undefined
          : url.pathname.match(/^\/(?:shorts|live|embed)\/([\w-]{11})\/?$/)?.[1]
        : undefined
  if (!id || (!bili && !/^[\w-]{11}$/.test(id))) {
    throw new Error('请输入 B站 BV 视频页或 YouTube 视频链接；B站短链请先打开后复制完整链接')
  }
  const source = bili ? 'bilibili' : 'youtube'
  // 保留分 P、播放列表和时间等参数；平台与视频 ID 用于列表去重。
  return {
    source, id, url: url.href, title: `${bili ? 'B站' : 'YouTube'} 视频 · ${id}`,
    author: '', thumbnail: '', duration: 0, playCount: 0,
  }
}
