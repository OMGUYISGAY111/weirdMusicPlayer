<script setup lang="ts">
import { computed, onBeforeUnmount } from 'vue'
import AudioPlayer from './AudioPlayer.vue'
import { usePlaylistStore } from '@/stores/playlist'
import { createHostMediaController } from '@/playback/hostMediaController'
import type { PlaybackMode } from '@/playlist/playlist'
import { host, hostBridge } from '@/bridge/host'

// 应用级播放端：路由页面卸载时仍保持同一个控制器。
// 播放核心只有一个（切列表只换内容），所以这里连接一次即可。
const playlist = usePlaylistStore()
const controller = createHostMediaController()
const disconnect = playlist.connectPlayer(controller)
const mode = computed({ get: () => playlist.state.mode, set: (value: PlaybackMode) => playlist.setMode(value) })
// 先读取共享快照以跟踪队列、模式和历史变化，再向核心查询可用操作。
const canNext = computed(() => playlist.state.items.length > 0 && playlist.canNext())
const canPrevious = computed(() => playlist.state.currentId !== null && playlist.canPrevious())
const statusText = computed(() => {
  const status = playlist.state.status
  if (status === 'error') {
    const wait = playlist.state.autoRetryIn
    return wait > 0 ? `${playlist.state.error}（${wait} 秒后自动重试）` : playlist.state.error
  }
  if (status === 'paused' && !playlist.state.source) return '已恢复列表，点击播放'
  return { idle: '选择歌曲开始播放', resolving: '正在解析音频…', loading: '正在加载音频…', buffering: '缓冲中，网络恢复后继续播放…', playing: '正在播放', paused: '已暂停', ended: '列表播放完毕' }[status]
})
onBeforeUnmount(() => { disconnect(); controller.dispose() })
</script>

<template>
  <div v-show="playlist.state.items.length" class="queue-dock">
    <div class="queue-controls">
      <RouterLink to="/search">{{ playlist.playerName }}（{{ playlist.state.items.length }}）</RouterLink>
      <button type="button" :disabled="!hostBridge.ready" @click="host.openLogFolder()">打开日志</button>
      <label>播放模式
        <select v-model="mode" aria-label="播放模式">
          <option value="sequential">顺序播放</option>
          <option value="repeat-all">列表循环</option>
          <option value="repeat-one">单曲循环</option>
          <option value="shuffle">随机播放</option>
        </select>
      </label>
      <button type="button" :disabled="!canPrevious" @click="playlist.previous()">上一首</button>
      <button type="button" :disabled="!canNext" @click="playlist.next()">下一首</button>
      <button v-if="!playlist.state.source && ['idle', 'paused'].includes(playlist.state.status)" type="button" @click="playlist.resume()">播放</button>
      <button v-if="playlist.state.status === 'resolving'" type="button" @click="playlist.pause()">取消播放</button>
      <button v-if="playlist.state.status === 'error'" type="button" @click="playlist.play()">重试</button>
      <span role="status">{{ statusText }}</span>
    </div>
    <AudioPlayer
      :controller="controller"
      :src="playlist.state.source?.url || (playlist.state.source?.file ? 'local-file' : '')"
      :file="playlist.state.source?.file"
      :title="playlist.currentTrack?.title || '播放列表'"
      :artist="playlist.currentTrack?.artist"
      :cover="playlist.currentTrack?.cover"
    />
  </div>
</template>

<style scoped>
.queue-dock { position: fixed; z-index: 20; bottom: 0; left: 0; right: 0; padding: 10px 28px 20px; background: linear-gradient(to top, var(--mp-scrim) 85%, transparent); }
.queue-dock > * { max-width: 1080px; margin: 0 auto; }
.queue-controls { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; padding: 8px 0; font-size: 12px; }
.queue-controls a { color: var(--mp-accent); }
.queue-controls label { display: flex; align-items: center; gap: 6px; }
.queue-controls select { color: var(--mp-text); background: var(--mp-panel); border: 1px solid var(--mp-border); border-radius: 6px; padding: 3px 6px; font: inherit; }
.queue-controls button { padding: 4px 9px; font-size: 12px; }
.queue-controls span { color: var(--mp-dim); overflow-wrap: anywhere; }
</style>
