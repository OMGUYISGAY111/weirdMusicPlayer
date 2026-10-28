import type { PlaybackController } from '../playback/types'
import type { createPlaylist } from './playlist'

/** 连接播放端的命令与事件。播放器本身无需知道队列或播放模式。 */
export function connectPlaylistPlayback(queue: ReturnType<typeof createPlaylist>, controller: PlaybackController): () => void {
  const disconnect = queue.connect({
    load: (source) => controller.load(source),
    pause: () => controller.pause(),
    async play() {
      await controller.play()
      const error = controller.snapshot().error
      if (error) throw new Error(error)
    },
  })
  const off = [
    controller.on('statechange', () => {
      const state = controller.snapshot()
      queue.syncBuffering(state.loading, state.playing)
    }),
    controller.on('ended', () => { void queue.ended() }),
    controller.on('play', () => queue.syncPlaying(true)),
    controller.on('pause', () => {
      // HTML 媒体自然结束可能先发 pause 再发 ended，避免将其当成用户暂停。
      if (!controller.element?.ended) queue.syncPlaying(false)
    }),
    controller.on('error', () => queue.fail(controller.snapshot().error ?? '音频播放失败')),
  ]
  return () => { off.forEach((unsubscribe) => unsubscribe()); disconnect() }
}
