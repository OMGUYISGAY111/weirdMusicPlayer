<script setup lang="ts">
import type { PlaylistTrack } from '@/playlist/playlist'

defineProps<{
  items: readonly PlaylistTrack[]
  currentId: string | null
  busy?: boolean
}>()
const emit = defineEmits<{
  play: [id: string]
  remove: [id: string]
  clear: []
}>()
</script>

<template>
  <section class="playlist" aria-label="播放列表">
    <header>
      <h2>播放列表 <small>{{ items.length }} 首</small></h2>
      <button type="button" :disabled="!items.length" @click="emit('clear')">清空列表</button>
    </header>
    <p v-if="!items.length" class="empty">还没有歌曲，可以从搜索结果加入列表。</p>
    <ol v-else>
      <li v-for="(item, index) in items" :key="item.id" :class="{ current: item.id === currentId }">
        <span class="number">{{ index + 1 }}</span>
        <img v-if="item.cover" class="cover" :src="item.cover" alt="" loading="lazy" referrerpolicy="no-referrer" @error="($event.target as HTMLImageElement).hidden = true" />
        <div class="info">
          <strong>{{ item.title }}</strong>
          <small>{{ item.artist || '未知作者' }} · {{ item.source.kind === 'audio' ? '音频' : item.source.kind === 'bilibili' ? 'B站' : 'YouTube' }}</small>
        </div>
        <span v-if="item.id === currentId" class="current-label">{{ busy ? '解析中…' : '当前曲目' }}</span>
        <button type="button" :aria-label="`播放 ${item.title}`" @click="emit('play', item.id)">播放</button>
        <button type="button" :aria-label="`移除 ${item.title}`" @click="emit('remove', item.id)">移除</button>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.playlist { padding: 18px; background: var(--panel); border: 1px solid var(--border); border-radius: 14px; }
header, li { display: flex; align-items: center; gap: 12px; }
header { justify-content: space-between; }
h2 { margin: 0; font-size: 17px; }
small, .empty, .number { color: var(--text-dim); font-size: 12px; }
h2 small { margin-left: 8px; font-weight: normal; }
ol { list-style: none; padding: 0; margin: 12px 0 0; max-height: 380px; overflow: auto; }
li { padding: 10px 6px; border-radius: 8px; }
li.current { background: rgba(124, 92, 255, .13); }
.number { width: 22px; text-align: center; }
.cover { width: 42px; height: 42px; object-fit: cover; border-radius: 6px; flex-shrink: 0; }
.info { flex: 1; min-width: 0; display: grid; gap: 5px; }
strong { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
button { padding: 6px 10px; font-size: 12px; flex-shrink: 0; }
.current-label { color: var(--accent-2); font-size: 12px; }
@media (max-width: 600px) { .current-label { display: none; } li { gap: 6px; } }
</style>
