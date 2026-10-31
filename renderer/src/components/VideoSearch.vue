<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { searchSongVideos, songKeyword } from '@/search/songVideoSearch'
import AppHeader from './AppHeader.vue'
import { resolveVideoLink } from '@/search/videoLink'
import {
  host,
  hostBridge,
  type MediaSearchItem,
  type MediaSearchResult,
  type MediaSearchSource,
} from '@/bridge/host'

/**
 * 按歌曲信息搜视频：填「歌曲名 / 作者 / 专辑」，同时搜 B站 和 YouTube，列出可点的视频链接。
 *
 * 搜索本身在宿主里做（renderer 直接 fetch 会被 CORS / CSP 拦住），
 * 这里只负责拼关键词、并发调用、展示结果：
 *   - 「打开」/ 点标题 → 交给宿主用系统浏览器打开（不会把 CEF 窗口导航走）
 *   - 「复制链接」      → 复制视频页地址
 *   - 「立即播放」      → emit('play', item)，由父页面决定如何播放
 *   - 「加入列表」      → emit('add', item)，组件不依赖列表实现
 */

interface Props {
  /** 每个平台最多返回几条（1~30，宿主侧也会夹紧） */
  limit?: number
}

const props = withDefaults(defineProps<Props>(), { limit: 6 })

const emit = defineEmits<{
  /** 每个平台完成时返回实际视频链接；可由父页面保存或加入列表。 */
  result: [result: MediaSearchResult]
  /** 用户点了「立即播放」，参数是选中那条视频 */
  play: [item: MediaSearchItem]
  add: [item: MediaSearchItem]
  dockResize: [height: number]
}>()

type PanelState = 'idle' | 'loading' | 'done' | 'empty' | 'error'

interface Panel {
  key: MediaSearchSource
  label: string
  hint: string
  state: PanelState
  items: MediaSearchItem[]
  error: string
}

function createPanel(key: MediaSearchSource, label: string, hint: string): Panel {
  return { key, label, hint, state: 'idle', items: [], error: '' }
}

const song = defineModel<string>('song', { default: '' })
const artist = defineModel<string>('artist', { default: '' })
const album = defineModel<string>('album', { default: '' })
const mode = ref<'song' | 'link'>('song')
const videoUrl = ref('')
const linkItem = ref<MediaSearchItem | null>(null)
const linkError = ref('')
const linkLoading = ref(false)
let linkSequence = 0
watch(videoUrl, () => {
  linkSequence += 1
  linkLoading.value = false
  linkItem.value = null
  linkError.value = ''
}, { flush: 'sync' })
watch(mode, () => { linkSequence += 1; linkLoading.value = false })

// reactive 对象本身是代理，模板里直接读属性就能跟踪
const bili = reactive<Panel>(createPanel('bilibili', '哔哩哔哩', 'bilibili.com'))
const youtube = reactive<Panel>(createPanel('youtube', 'YouTube', 'youtube.com'))
const panels: Panel[] = [bili, youtube]

const requestSeq = ref(0)
const copiedUrl = ref('')

/** 搜索关键词 = 三个输入框里非空的字段，用空格拼起来 */
const keyword = computed(() =>
  songKeyword({ song: song.value, artist: artist.value, album: album.value }),
)

const searching = computed(() => linkLoading.value || panels.some((panel) => panel.state === 'loading'))
const canSearch = computed(() => (mode.value === 'song' ? song.value.trim() : videoUrl.value.trim()).length > 0 && !searching.value)
const inHost = computed(() => hostBridge.ready)

/** 宿主不可用（浏览器预览）或某平台报错时，给一个能直接点的网页版搜索 */
const fallbackLinks = computed(() => {
  const query = keyword.value.trim()
  if (!query) return null
  const encoded = encodeURIComponent(query)
  return {
    bilibili: `https://search.bilibili.com/all?keyword=${encoded}`,
    youtube: `https://www.youtube.com/results?search_query=${encoded}`,
  }
})

function applyResult(panel: Panel, result: MediaSearchResult): void {
  panel.items = result.items
  panel.error = result.error ?? ''
  if (!result.ok || panel.error) {
    panel.state = 'error'
  } else {
    panel.state = panel.items.length > 0 ? 'done' : 'empty'
  }
}

async function search(): Promise<void> {
  if (!canSearch.value) return

  if (mode.value === 'link') {
    const seq = ++linkSequence
    linkError.value = ''
    linkItem.value = null
    linkLoading.value = true
    try {
      const item = await resolveVideoLink(videoUrl.value, {
        bilibili: (url) => host.getBilibiliVideoInfo(url),
        youtube: (url) => host.getYouTubeVideoInfo(url),
      })
      if (seq === linkSequence) linkItem.value = item
    } catch (error) {
      if (seq === linkSequence) linkError.value = error instanceof Error ? error.message : String(error)
    } finally {
      if (seq === linkSequence) linkLoading.value = false
    }
    return
  }

  const seq = ++requestSeq.value
  copiedUrl.value = ''
  for (const panel of panels) {
    panel.state = 'loading'
    panel.error = ''
    panel.items = []
  }

  await searchSongVideos(
    { song: song.value, artist: artist.value, album: album.value },
    {
      bilibili: (query, limit) => host.searchBilibili(query, limit),
      youtube: (query, limit) => host.searchYouTube(query, limit),
    },
    props.limit,
    (result) => {
      if (seq !== requestSeq.value) return
      applyResult(result.source === 'bilibili' ? bili : youtube, result)
      emit('result', result)
    },
  )
}

onBeforeUnmount(() => { requestSeq.value += 1; linkSequence += 1 })
defineExpose({ search, clear })

function clear(): void {
  requestSeq.value += 1
  song.value = ''
  artist.value = ''
  album.value = ''
  videoUrl.value = ''
  linkItem.value = null
  linkError.value = ''
  copiedUrl.value = ''
  for (const panel of panels) {
    panel.state = 'idle'
    panel.error = ''
    panel.items = []
  }
}

function openItem(item: MediaSearchItem): void {
  void host.openExternalUrl(item.url)
}

async function copyItem(item: MediaSearchItem): Promise<void> {
  const ok = await copyText(item.url)
  copiedUrl.value = ok ? item.url : ''
  if (ok) {
    window.setTimeout(() => {
      if (copiedUrl.value === item.url) copiedUrl.value = ''
    }, 1600)
  }
}

/** 宿主没有「写剪贴板」接口，优先用 navigator.clipboard，再退回 execCommand */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // 非安全上下文 / 没权限：继续走兼容方案
  }

  try {
    const area = document.createElement('textarea')
    area.value = text
    area.setAttribute('readonly', '')
    area.style.position = 'fixed'
    area.style.opacity = '0'
    document.body.appendChild(area)
    area.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(area)
    return ok
  } catch {
    return false
  }
}

/** 封面挂了就直接藏掉，留深色占位，不让破图占版面 */
function hideBrokenImage(event: Event): void {
  const image = event.target as HTMLImageElement | null
  if (image) image.hidden = true
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return ''
  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const rest = total % 60
  const pad = (value: number): string => String(value).padStart(2, '0')
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(rest)}` : `${minutes}:${pad(rest)}`
}

function formatPlayCount(count: number): string {
  if (!Number.isFinite(count) || count <= 0) return ''
  if (count >= 100_000_000) return `${(count / 100_000_000).toFixed(1)} 亿播放`
  if (count >= 10_000) return `${(count / 10_000).toFixed(1)} 万播放`
  return `${count} 播放`
}

function panelFallback(panel: Panel): string {
  return fallbackLinks.value?.[panel.key] ?? ''
}
</script>

<template>
  <section class="vs">
    <header class="vs-head">
      <div>
        <h2 class="vs-title">搜索音乐</h2>
        <p class="vs-sub">
          输入歌曲信息搜索视频，或切换模式粘贴 B站 / YouTube 视频链接。
        </p>
      </div>
      <span class="vs-pill" :class="inHost ? 'ok' : 'warn'">
        {{ inHost ? '宿主已连接' : '浏览器预览（无宿主）' }}
      </span>
    </header>

    <AppHeader v-model:song="song" v-model:artist="artist" v-model:album="album"
      v-model:url="videoUrl" v-model:mode="mode" :busy="searching" :can-submit="canSearch"
      @submit="search" @dock-resize="emit('dockResize', $event)" />

    <template v-if="mode === 'link'">
      <p v-if="linkLoading" class="vs-state" role="status">正在读取视频标题、封面和作者…</p>
      <p v-else-if="linkError" class="vs-state error" role="alert">{{ linkError }}</p>
      <section v-else-if="linkItem" class="vs-panel" aria-live="polite">
        <div class="vs-link-summary">
          <img v-if="linkItem.thumbnail" class="vs-link-cover" :src="linkItem.thumbnail" alt="视频封面" referrerpolicy="no-referrer" @error="hideBrokenImage" />
          <div class="vs-info">
            <h3 class="vs-title">{{ linkItem.title }}</h3>
            <p class="vs-meta">
              <span v-if="linkItem.author">{{ linkItem.author }}</span>
              <span v-if="formatDuration(linkItem.duration)">{{ formatDuration(linkItem.duration) }}</span>
            </p>
          </div>
        </div>
        <p class="vs-state">{{ linkItem.url }}</p>
        <div class="vs-item-actions">
          <button type="button" class="vs-play" @click="emit('play', linkItem)">立即播放</button>
          <button type="button" @click="emit('add', linkItem)">加入列表</button>
          <button type="button" @click="openItem(linkItem)">打开</button>
        </div>
      </section>
      <p v-else class="vs-state">粘贴完整视频链接，按 Enter 或点击搜索图标，然后播放或加入列表。</p>
    </template>

    <p v-if="mode === 'song'" class="vs-query">
      关键词：<code>{{ keyword || '（还没填）' }}</code>
      <span v-if="searching" class="vs-query-busy">搜索中，YouTube 首次通常要 3~10 秒…</span>
    </p>

    <div v-if="mode === 'song'" class="vs-grid">
      <section v-for="panel in panels" :key="panel.key" class="vs-panel" aria-live="polite" :aria-busy="panel.state === 'loading'">
        <header class="vs-panel-head">
          <h3>
            <span class="vs-dot" :class="panel.key" />
            {{ panel.label }}
          </h3>
          <span class="vs-panel-meta">
            <template v-if="panel.state === 'done'">{{ panel.items.length }} 条</template>
            <template v-else>{{ panel.hint }}</template>
          </span>
        </header>

        <p v-if="panel.state === 'loading'" class="vs-state">正在搜索 {{ panel.label }}…</p>

        <div v-else-if="panel.state === 'error'" class="vs-state error">
          <p>{{ panel.error }}</p>
          <a
            v-if="panelFallback(panel)"
            :href="panelFallback(panel)"
            target="_blank"
            rel="noreferrer"
            @click.prevent="host.openExternalUrl(panelFallback(panel))"
          >
            在浏览器里打开 {{ panel.label }} 搜索页 →
          </a>
        </div>

        <p v-else-if="panel.state === 'empty'" class="vs-state">
          没搜到结果。试试只填歌名，或者换个写法。
        </p>

        <p v-else-if="panel.state === 'idle'" class="vs-state">输入歌名，歌手和专辑可选，按 Enter 或点击搜索图标。</p>

        <ol v-else class="vs-list">
          <li v-for="item in panel.items" :key="item.id" class="vs-item">
            <a
              class="vs-thumb"
              :href="item.url"
              target="_blank"
              rel="noreferrer"
              :title="`在浏览器打开：${item.title}`"
              @click.prevent="openItem(item)"
            >
              <img
                v-if="item.thumbnail"
                :src="item.thumbnail"
                alt=""
                loading="lazy"
                referrerpolicy="no-referrer"
                @error="hideBrokenImage"
              />
              <span v-else class="vs-thumb-fallback" aria-hidden="true">▶</span>
              <span v-if="formatDuration(item.duration)" class="vs-duration">
                {{ formatDuration(item.duration) }}
              </span>
            </a>

            <div class="vs-info">
              <a
                class="vs-item-title"
                :href="item.url"
                target="_blank"
                rel="noreferrer"
                :title="item.title"
                @click.prevent="openItem(item)"
              >
                {{ item.title }}
              </a>
              <p class="vs-meta">
                <span v-if="item.author" class="vs-author" :title="item.author">{{ item.author }}</span>
                <span v-if="formatPlayCount(item.playCount)">{{ formatPlayCount(item.playCount) }}</span>
              </p>
              <div class="vs-item-actions">
                <button type="button" class="vs-play" @click="emit('play', item)">立即播放</button>
                <button type="button" @click="emit('add', item)">加入列表</button>
                <button type="button" @click="openItem(item)">打开</button>
                <button type="button" @click="copyItem(item)">
                  {{ copiedUrl === item.url ? '已复制' : '复制链接' }}
                </button>
              </div>
            </div>
          </li>
        </ol>
      </section>
    </div>
  </section>
</template>

<style scoped>
.vs {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.vs-link-summary { display: flex; align-items: flex-start; gap: 14px; }
.vs-link-cover { width: 144px; max-width: 35%; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 8px; background: var(--mp-track); }

.vs-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 14px;
}

.vs-title {
  margin: 0 0 6px;
  font-size: 17px;
  letter-spacing: 0.2px;
}

.vs-sub {
  margin: 0;
  max-width: 640px;
  font-size: 12.5px;
  line-height: 1.65;
  color: var(--mp-dim);
  user-select: text;
}

.vs-pill {
  flex: 0 0 auto;
  font-size: 12px;
  padding: 5px 11px;
  border-radius: 999px;
  border: 1px solid var(--mp-border);
  white-space: nowrap;
}

.vs-pill.ok {
  color: var(--mp-ok);
  border-color: color-mix(in srgb, var(--mp-ok) 40%, transparent);
  background: color-mix(in srgb, var(--mp-ok) 10%, transparent);
}

.vs-pill.warn {
  color: var(--mp-warn);
  border-color: color-mix(in srgb, var(--mp-warn) 40%, transparent);
  background: color-mix(in srgb, var(--mp-warn) 10%, transparent);
}

.vs-query {
  margin: 0;
  font-size: 12.5px;
  color: var(--mp-dim);
  user-select: text;
}

.vs-query code {
  color: var(--mp-text);
}

.vs-query-busy {
  margin-left: 10px;
  color: var(--mp-warn);
}

.vs-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

.vs-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 132px;
  padding: 14px 16px 16px;
  border: 1px solid var(--mp-border);
  border-radius: 14px;
  background: var(--mp-panel);
}

.vs-panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.vs-panel-head h3 {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13.5px;
  font-weight: 600;
}

/* 平台标记点：统一用主题红，平台名由标题文字区分。 */
.vs-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--mp-accent);
}

.vs-panel-meta {
  font-size: 11.5px;
  color: var(--mp-dim);
}

.vs-state {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.7;
  color: var(--mp-dim);
  user-select: text;
}

.vs-state.error {
  color: var(--mp-danger);
}

.vs-state.error p {
  margin: 0 0 6px;
  white-space: pre-wrap;
  word-break: break-word;
}

.vs-state a {
  color: var(--mp-accent);
  font-size: 12.5px;
  text-decoration: none;
}

.vs-state a:hover {
  text-decoration: underline;
}

.vs-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.vs-item {
  display: flex;
  gap: 11px;
  padding: 7px;
  border-radius: 10px;
  transition: background 0.12s ease;
}

.vs-item:hover {
  background: var(--mp-hover);
}

.vs-thumb {
  position: relative;
  flex: 0 0 108px;
  height: 61px;
  display: grid;
  place-items: center;
  overflow: hidden;
  border-radius: 8px;
  background: var(--mp-track);
  border: 1px solid var(--mp-border);
}

.vs-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.vs-thumb-fallback {
  color: var(--mp-dim);
  font-size: 14px;
}

.vs-duration {
  position: absolute;
  right: 4px;
  bottom: 4px;
  padding: 1px 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--mp-text) 72%, transparent);
  color: var(--mp-on-accent);
  font-size: 10.5px;
  font-variant-numeric: tabular-nums;
}

.vs-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.vs-item-title {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  color: var(--mp-text);
  font-size: 13px;
  line-height: 1.45;
  text-decoration: none;
}

.vs-item-title:hover {
  color: var(--mp-accent);
  text-decoration: underline;
}

.vs-meta {
  margin: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 11.5px;
  color: var(--mp-dim);
}

.vs-author {
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.vs-item-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 2px;
}

.vs-item-actions button {
  padding: 4px 9px;
  font-size: 11.5px;
  border-radius: 8px;
}

.vs-item-actions button.vs-play {
  border-color: transparent;
  background: var(--mp-accent);
  color: var(--mp-on-accent);
  font-weight: 600;
}

.vs-item-actions button.vs-play:hover {
  border-color: transparent;
  background: color-mix(in srgb, var(--mp-accent) 88%, var(--mp-text));
}

@media (max-width: 820px) {
  .vs-grid {
    grid-template-columns: minmax(0, 1fr);
  }

}
</style>
