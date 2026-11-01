<script setup lang="ts">
import { computed, ref } from 'vue'
import Playlist from '@/components/Playlist.vue'
import PlaylistBar from '@/components/PlaylistBar.vue'
import VideoSearch from '@/components/VideoSearch.vue'
import type { MediaSearchItem } from '@/bridge/host'
import type { PlaylistOption } from '@/playlist/library'
import { usePlaylistStore } from '@/stores/playlist'
import { trackFromSearch } from '@/playlist/hostAdapter'

/**
 * 页面只负责把搜索结果转换为列表操作；解析、播放和列表管理都在 store 里。
 * 搜索页顶部是列表栏（切换 / 新建 / 重命名 / 删除 / 导入 / 导出），
 * 下面是当前列表的曲目；搜索结果的「加入列表」会弹出目标列表选择。
 */

const playlist = usePlaylistStore()
const status = ref('搜索视频后可以加入列表，或立即播放。')
const transfer = ref('')
const transferring = ref(false)
const searchDockHeight = ref(92)

const playlistOptions = computed<PlaylistOption[]>(() => playlist.playlists.map((record) => ({
  id: record.id,
  name: record.name,
  count: record.items.length,
  active: record.id === playlist.activeId,
})))

function onAdd(item: MediaSearchItem, playlistId: string | null): void {
  const track = trackFromSearch(item)
  const result = playlist.addTo(playlistId, track)
  status.value = result.existing
    ? `「${result.playlistName}」里已有：${item.title}`
    : `已加入「${result.playlistName}」：${item.title}`
}

async function onPlay(item: MediaSearchItem): Promise<void> {
  const track = trackFromSearch(item)
  playlist.addTo(playlist.activeId, track)
  status.value = `正在播放：${item.title}`
  await playlist.playFrom(playlist.activeId, track.id)
}

async function onImport(): Promise<void> {
  if (transferring.value) return
  transferring.value = true
  transfer.value = ''
  try {
    const result = await playlist.importFromFile()
    if (result.message) transfer.value = result.message
  } finally {
    transferring.value = false
  }
}

async function onExport(): Promise<void> {
  if (transferring.value) return
  transferring.value = true
  transfer.value = ''
  try {
    const result = await playlist.exportToFile()
    if (result.message) transfer.value = result.message
  } finally {
    transferring.value = false
  }
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
        点「加入列表」可以选目标列表，点「立即播放」加入当前列表并开始播放，播完自动接下一首。
      </p>
    </header>

    <VideoSearch
      :playlists="playlistOptions"
      @play="onPlay"
      @add="onAdd"
      @dock-resize="searchDockHeight = $event"
    />

    <p class="hint" role="status">{{ status }}</p>
    <p v-if="playlist.storageError" class="hint" role="status">{{ playlist.storageError }}</p>
    <p v-if="transfer" class="hint" role="status">{{ transfer }}</p>

    <PlaylistBar
      :playlists="playlist.playlists"
      :active-id="playlist.activeId"
      :player-id="playlist.playerListId"
      :busy="playlist.state.status === 'resolving'"
      :transfer-busy="transferring"
      @select="playlist.activate($event)"
      @create="playlist.create()"
      @rename="(id, name) => playlist.rename(id, name)"
      @remove="playlist.removePlaylist($event)"
      @import="onImport"
      @export="onExport"
    />

    <Playlist
      :title="playlist.active.name"
      :items="playlist.activeItems"
      :current-id="playlist.activeCurrentId"
      :busy="playlist.activeId === playlist.playerListId && playlist.state.status === 'resolving'"
      @play="playlist.playFrom(playlist.activeId, $event)"
      @remove="playlist.removeTrack($event)"
      @clear="playlist.clearActive()"
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
