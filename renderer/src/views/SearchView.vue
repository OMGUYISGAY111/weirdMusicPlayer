<script setup lang="ts">
import { ref } from 'vue'
import Playlist from '@/components/Playlist.vue'
import VideoSearch from '@/components/VideoSearch.vue'
import type { MediaSearchItem } from '@/bridge/host'
import { usePlaylistStore } from '@/stores/playlist'
import { trackFromSearch } from '@/playlist/hostAdapter'

/**
 * 页面只负责把搜索事件转换为列表操作；解析和播放由列表适配层处理。
 */

const playlist = usePlaylistStore()
const status = ref('搜索视频后可以加入列表，或立即播放。')
const searchDockHeight = ref(92)

function onAdd(item: MediaSearchItem): string {
  const track = trackFromSearch(item)
  const exists = playlist.state.items.some((entry) => entry.id === track.id)
  const id = playlist.add(track)
  if (exists && (track.cover || track.artist)) playlist.updateMetadata(id, track)
  status.value = exists ? `列表中已有：${item.title}` : `已加入列表：${item.title}`
  return id
}

async function onPlay(item: MediaSearchItem): Promise<void> {
  await playlist.play(onAdd(item))
}
</script>

<template>
  <main class="page" :style="{ paddingTop: `${searchDockHeight + 24}px` }">
    <header class="head">
      <nav class="nav">
        <RouterLink to="/">← 首页</RouterLink>
        <RouterLink to="/player">播放器调试页</RouterLink>
      </nav>
      <h1>搜视频 · 找链接 · 直接听</h1>
      <p>
        输入歌曲名 / 歌手 / 专辑，同时搜索 B站 和 YouTube；也可以切换为视频链接模式。
        点「加入列表」收藏到当前播放队列，点「立即播放」加入列表并开始播放，播完自动接下一首。
      </p>
    </header>

    <VideoSearch @play="onPlay" @add="onAdd" @dock-resize="searchDockHeight = $event" />

    <p class="hint" role="status">{{ status }}</p>
    <p v-if="playlist.storageError" class="hint" role="status">{{ playlist.storageError }}</p>

    <Playlist
      :items="playlist.state.items"
      :current-id="playlist.state.currentId"
      :busy="playlist.state.status === 'resolving'"
      @play="playlist.play($event)"
      @remove="playlist.remove($event)"
      @clear="playlist.clear()"
    />
  </main>
</template>

<style scoped>
.page {
  max-width: 1080px;
  margin: 0 auto;
  padding: 24px 28px 150px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.head h1 {
  margin: 10px 0 8px;
  font-size: 22px;
  letter-spacing: 0.2px;
}

.head p {
  margin: 0;
  max-width: 760px;
  color: var(--mp-dim);
  font-size: 13px;
  line-height: 1.7;
  user-select: text;
}

.nav {
  display: flex;
  gap: 14px;
  font-size: 12.5px;
}

.nav a {
  color: var(--mp-accent);
  text-decoration: none;
}

.nav a:hover {
  text-decoration: underline;
}

.hint {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--mp-dim);
  user-select: text;
  word-break: break-word;
}

</style>
