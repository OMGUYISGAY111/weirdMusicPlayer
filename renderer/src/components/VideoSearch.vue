<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { searchSongVideos, songKeyword } from '@/search/songVideoSearch'
import AppHeader from './AppHeader.vue'
import { SEARCH_SOURCES, sourcesForMode, type SearchSource } from '@/search/hostSources'
import { resolveVideoLink } from '@/search/videoLink'
import type { SearchModeKey } from '@/search/modes'
import type { PlaylistOption } from '@/playlist/library'
import {
  host,
  hostBridge,
  type MediaSearchItem,
  type MediaSearchResult,
  type MediaSearchSource,
} from '@/bridge/host'

/**
 * 按歌曲信息搜视频：填「歌曲名 / 作者 / 专辑」，同时搜当前模式涉及的站点，列出可点的视频链接。
 *
 * 分工：
 *   - 入口（AppHeader / SearchBar）只管输入和模式，不认识结果；
 *   - 模式（search/modes.ts）声明涉及哪些站点，这里只按它渲染面板、执行搜索；
 *   - 站点差异（标题、域名、兜底搜索页、链接识别、元信息文案）全在 search/sources.ts；
 *   - 结果组件本身只画通用的卡片/列表，不写任何 `=== 'bilibili'` 分支。
 *
 * 交互：
 *   - 「打开」/ 点标题 → 交给宿主用系统浏览器打开（不会把 CEF 窗口导航走）
 *   - 「复制链接」      → 复制视频页地址
 *   - 「立即播放」      → emit('play', item)，由父页面决定如何播放
 *   - 「加入列表」      → emit('add', item)，组件不依赖列表实现
 */

interface Props {
  /** 每个站点最多返回几条（1~30，宿主侧也会夹紧） */
  limit?: number
  /** 可加入的播放列表；为空时只能新建 */
  playlists?: readonly PlaylistOption[]
}

const props = withDefaults(defineProps<Props>(), { limit: 6, playlists: () => [] })

const emit = defineEmits<{
  /** 每个站点完成时返回实际视频链接；可由父页面保存或加入列表。 */
  result: [result: MediaSearchResult]
  /** 用户点了「立即播放」，参数是选中那条视频 */
  play: [item: MediaSearchItem]
  /** 加入列表：第二个参数是目标列表 id；null 表示「新建一个列表并加入」 */
  add: [item: MediaSearchItem, playlistId: string | null]
  dockResize: [height: number]
}>()

type PanelState = 'idle' | 'loading' | 'done' | 'empty' | 'error'

/** 一个站点的结果面板；站点能力全在 source 里，面板只存状态 */
interface Panel {
  source: SearchSource
  key: MediaSearchSource
  state: PanelState
  items: MediaSearchItem[]
  error: string
}

function createPanel(source: SearchSource): Panel {
  return { source, key: source.key, state: 'idle', items: [], error: '' }
}

const song = defineModel<string>('song', { default: '' })
const artist = defineModel<string>('artist', { default: '' })
const album = defineModel<string>('album', { default: '' })
const mode = ref<SearchModeKey>('song')
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

// 每个已知站点一个面板，顺序即注册表顺序；具体显示哪些由当前模式决定
const panels = SEARCH_SOURCES.map((source) => reactive(createPanel(source)))
const activePanels = computed(() => {
  const keys = new Set(sourcesForMode(mode.value).map((source) => source.key))
  return panels.filter((panel) => keys.has(panel.key))
})

const requestSeq = ref(0)
const copiedUrl = ref('')

/** 搜索关键词 = 三个输入框里非空的字段，用空格拼起来 */
const keyword = computed(() =>
  songKeyword({ song: song.value, artist: artist.value, album: album.value }),
)

const searching = computed(() => linkLoading.value || panels.some((panel) => panel.state === 'loading'))
const canSearch = computed(() => (mode.value === 'song' ? song.value.trim() : videoUrl.value.trim()).length > 0 && !searching.value)
const inHost = computed(() => hostBridge.ready)
/** 支持粘贴哪些站点的链接，由注册表决定，不在模板里写死 */
const supportedSites = SEARCH_SOURCES.map((source) => source.kindLabel).join(' / ')

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
      const item = await resolveVideoLink(videoUrl.value, sourcesForMode('link'))
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
    sourcesForMode('song'),
    props.limit,
    (result) => {
      if (seq !== requestSeq.value) return
      const panel = panels.find((entry) => entry.key === result.source)
      if (panel) applyResult(panel, result)
      emit('result', result)
    },
  )
}

onBeforeUnmount(() => {
  requestSeq.value += 1
  linkSequence += 1
  document.removeEventListener('pointerdown', onDocumentPointerDown, true)
  document.removeEventListener('keydown', onDocumentKeydown)
  document.removeEventListener('scroll', onViewportChange, true)
  window.removeEventListener('resize', onViewportChange)
})
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

// ── 「加入列表」时选择目标列表的浮层 ──────────────────────────────────
const addMenu = ref<{ item: MediaSearchItem } | null>(null)
const addMenuEl = ref<HTMLElement | null>(null)
const addMenuStyle = ref<{ left: string; top: string; visibility: 'hidden' | 'visible' }>({
  left: '0px',
  top: '0px',
  visibility: 'hidden',
})

async function openAddMenu(item: MediaSearchItem, event: MouseEvent): Promise<void> {
  const anchor = (event.currentTarget as HTMLElement | null)?.getBoundingClientRect()
  if (!anchor) return
  addMenuStyle.value = { left: `${Math.round(anchor.left)}px`, top: `${Math.round(anchor.bottom + 6)}px`, visibility: 'hidden' }
  addMenu.value = { item }
  await nextTick()
  const box = addMenuEl.value?.getBoundingClientRect()
  if (!box) return
  const left = Math.min(Math.max(8, anchor.left), Math.max(8, window.innerWidth - box.width - 8))
  const top = Math.min(Math.max(8, anchor.bottom + 6), Math.max(8, window.innerHeight - box.height - 8))
  addMenuStyle.value = { left: `${Math.round(left)}px`, top: `${Math.round(top)}px`, visibility: 'visible' }
}

function chooseTarget(playlistId: string | null): void {
  const current = addMenu.value
  if (!current) return
  addMenu.value = null
  emit('add', current.item, playlistId)
}

function onDocumentPointerDown(event: PointerEvent): void {
  if (!addMenu.value) return
  const target = event.target
  if (target instanceof Node && addMenuEl.value?.contains(target)) return
  if (target instanceof Element && target.closest('.vs-add')) return
  addMenu.value = null
}

function onDocumentKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !addMenu.value) return
  event.preventDefault()
  addMenu.value = null
}

/** 页面滚动 / 缩放后锚点会跑偏，直接收起浮层 */
function onViewportChange(): void {
  if (addMenu.value) addMenu.value = null
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown, true)
  document.addEventListener('keydown', onDocumentKeydown)
  document.addEventListener('scroll', onViewportChange, true)
  window.addEventListener('resize', onViewportChange)
})

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

/** 站点专属的元信息文案（播放量写法等）；没配就只显示作者 */
function metaText(panel: Panel, item: MediaSearchItem): string[] {
  return panel.source.formatMeta?.(item) ?? []
}

/** 宿主不可用或该站点报错时，给这个站点自己的网页版搜索入口 */
function panelFallback(panel: Panel): string {
  const query = keyword.value.trim()
  return query ? panel.source.searchUrl(query) : ''
}
</script>

<template>
  <section class="vs">
    <header class="vs-head">
      <div>
        <h2 class="vs-title">搜索音乐</h2>
        <p class="vs-sub">
          输入歌曲信息搜索视频，或切换模式粘贴 {{ supportedSites }} 视频链接。
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
          <button type="button" class="vs-add" @click="openAddMenu(linkItem, $event)">加入列表</button>
          <button type="button" @click="openItem(linkItem)">打开</button>
        </div>
      </section>
      <p v-else class="vs-state">粘贴完整视频链接，按 Enter 或点击搜索图标，然后播放或加入列表。</p>
    </template>

    <p v-if="mode === 'song'" class="vs-query">
      关键词：<code>{{ keyword || '（还没填）' }}</code>
      <span v-if="searching" class="vs-query-busy">搜索中，首次可能需要 3~10 秒…</span>
    </p>

    <div v-if="mode === 'song'" class="vs-grid">
      <section v-for="panel in activePanels" :key="panel.key" class="vs-panel" aria-live="polite" :aria-busy="panel.state === 'loading'">
        <header class="vs-panel-head">
          <h3>
            <span class="vs-dot" :class="panel.key" />
            {{ panel.source.label }}
          </h3>
          <span class="vs-panel-meta">
            <template v-if="panel.state === 'done'">{{ panel.items.length }} 条</template>
            <template v-else>{{ panel.source.site }}</template>
          </span>
        </header>

        <p v-if="panel.state === 'loading'" class="vs-state">正在搜索 {{ panel.source.label }}…</p>

        <div v-else-if="panel.state === 'error'" class="vs-state error">
          <p>{{ panel.error }}</p>
          <a
            v-if="panelFallback(panel)"
            :href="panelFallback(panel)"
            target="_blank"
            rel="noreferrer"
            @click.prevent="host.openExternalUrl(panelFallback(panel))"
          >
            在浏览器里打开 {{ panel.source.label }} 搜索页 →
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
                <span v-for="text in metaText(panel, item)" :key="text">{{ text }}</span>
              </p>
              <div class="vs-item-actions">
                <button type="button" class="vs-play" @click="emit('play', item)">立即播放</button>
                <button type="button" class="vs-add" @click="openAddMenu(item, $event)">加入列表</button>
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

  <!-- 加入列表：选择目标列表的浮层（teleport 到 body，避免被列表裁剪） -->
  <Teleport to="body">
    <div
      v-if="addMenu"
      ref="addMenuEl"
      class="add-menu"
      :style="addMenuStyle"
      role="menu"
      aria-label="选择播放列表"
    >
      <p class="add-menu-title">加入播放列表</p>
      <button
        v-for="option in playlists"
        :key="option.id"
        type="button"
        role="menuitem"
        class="add-menu-item"
        @click="chooseTarget(option.id)"
      >
        <span class="add-menu-name">{{ option.name }}</span>
        <span class="add-menu-meta">{{ option.count }} 首<template v-if="option.active"> · 选中</template></span>
      </button>
      <p v-if="!playlists.length" class="add-menu-empty">还没有播放列表</p>
      <button type="button" role="menuitem" class="add-menu-item add-menu-new" @click="chooseTarget(null)">
        ＋ 新建列表并加入
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.vs {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.vs-link-summary { display: flex; align-items: flex-start; gap: 14px; }
.vs-link-cover { width: 144px; max-width: 35%; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 8px; background: var(--mp-track); }

/* 「加入列表」浮层：跟着按钮定位，超出屏幕时自动收进来 */
.add-menu {
  position: fixed;
  z-index: 70;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 200px;
  max-width: 280px;
  max-height: min(360px, 70vh);
  overflow-y: auto;
  padding: 6px;
  border: 1px solid var(--mp-border);
  border-radius: 12px;
  background: var(--mp-panel);
  box-shadow: var(--mp-shadow-lg);
}

.add-menu-title {
  margin: 2px 8px 4px;
  font-size: 11.5px;
  color: var(--mp-dim);
}

.add-menu-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  width: 100%;
  padding: 7px 10px;
  font-size: 12.5px;
  text-align: left;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--mp-text);
}

.add-menu-item:hover {
  background: var(--mp-hover);
  border-color: transparent;
  transform: none;
}

.add-menu-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.add-menu-meta {
  flex: 0 0 auto;
  font-size: 11px;
  color: var(--mp-dim);
  font-variant-numeric: tabular-nums;
}

.add-menu-new {
  justify-content: flex-start;
  color: var(--mp-accent);
  border-top: 1px solid var(--mp-border);
  border-radius: 0 0 8px 8px;
  margin-top: 2px;
  padding-top: 8px;
}

.add-menu-empty {
  margin: 4px 8px 6px;
  font-size: 11.5px;
  color: var(--mp-dim);
}

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
