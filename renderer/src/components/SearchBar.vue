<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useId } from 'vue'

withDefaults(defineProps<{ busy?: boolean; canSubmit?: boolean }>(), { busy: false, canSubmit: true })
const song = defineModel<string>('song', { default: '' })
const artist = defineModel<string>('artist', { default: '' })
const album = defineModel<string>('album', { default: '' })
const url = defineModel<string>('url', { default: '' })
const mode = defineModel<'song' | 'link'>('mode', { default: 'song' })
defineEmits<{ submit: [] }>()
const modes = [{ key: 'song', label: '歌曲名 · 歌手 · 专辑' }, { key: 'link', label: 'B站 / YouTube 链接' }] as const
const activeIndex = computed(() => modes.findIndex((item) => item.key === mode.value))
const hoveredIndex = ref<number | null>(null)
const captionMode = computed(() => modes[hoveredIndex.value ?? activeIndex.value]!)
const modeButtons = ref<HTMLButtonElement[]>([])
const detailsExpanded = ref(false)
const detailsId = useId()
const searchForm = ref<HTMLFormElement | null>(null)
const mainInput = ref<HTMLInputElement | null>(null)
const showControls = ref(false)

function selectSearch(event: MouseEvent): void {
  showControls.value = true
  if (event.target === event.currentTarget) mainInput.value?.focus()
}

function onOutsidePointerDown(event: PointerEvent): void {
  if (event.target instanceof Node && !searchForm.value?.contains(event.target)) {
    showControls.value = false
    hoveredIndex.value = null
    const focused = document.activeElement
    if (focused instanceof HTMLElement && searchForm.value?.contains(focused)) focused.blur()
  }
}

function onFocusOut(): void {
  queueMicrotask(() => {
    if (!searchForm.value?.contains(document.activeElement)) {
      showControls.value = false
      hoveredIndex.value = null
    }
  })
}

onMounted(() => document.addEventListener('pointerdown', onOutsidePointerDown, true))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onOutsidePointerDown, true))

function moveMode(event: KeyboardEvent): void {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const index = event.key === 'Home' ? 0 : event.key === 'End' ? modes.length - 1 : (activeIndex.value + 1) % modes.length
  mode.value = modes[index]!.key
  modeButtons.value[index]?.focus()
}
</script>

<template>
  <form ref="searchForm" class="search-control" :class="{ 'show-controls': showControls }" role="search"
    @focusin="showControls = true" @focusout="onFocusOut" @submit.prevent="$emit('submit')">
    <div class="search-capsule" :aria-busy="busy" @click="selectSearch">
      <button class="search-submit" type="submit" :disabled="!canSubmit || busy" :aria-label="busy ? '搜索中' : mode === 'song' ? '搜索歌曲' : '识别视频链接'" :title="mode === 'song' ? '搜索歌曲' : '识别视频链接'">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true">
          <circle cx="10.8" cy="10.8" r="6.8" />
          <path d="m16 16 4.5 4.5" />
        </svg>
      </button>
      <input v-if="mode === 'song'" ref="mainInput" v-model="song" class="song-field" aria-label="歌曲名（必填）" placeholder="输入歌曲名" autocomplete="off" required />
      <input v-else ref="mainInput" v-model="url" class="link-field" aria-label="B站或 YouTube 视频链接" placeholder="粘贴 B站 / YouTube 视频链接" autocomplete="off" type="url" required />
      <span v-if="busy" class="search-spinner" aria-hidden="true" />
    </div>
    <div class="mode-switch" role="radiogroup" aria-label="搜索模式" @keydown="moveMode">
      <button v-for="(item, index) in modes" :key="item.key" :ref="(el) => { if (el) modeButtons[index] = el as HTMLButtonElement }"
        class="mode-button" :class="{ selected: mode === item.key }" type="button" role="radio"
        :aria-checked="mode === item.key" :aria-label="item.label" :title="item.label" :tabindex="mode === item.key ? 0 : -1"
        :style="{ '--offset': index - activeIndex }"
        @mouseenter="hoveredIndex = index" @mouseleave="hoveredIndex = null" @click="mode = item.key">
        <span class="mode-dot" />
      </button>
    </div>
    <div class="mode-caption" aria-live="polite">
      <Transition name="caption" mode="out-in">
        <span :key="captionMode.key" :class="{ 'current-mode': captionMode.key === mode }">{{ captionMode.label }}</span>
      </Transition>
    </div>
    <template v-if="mode === 'song'">
      <button class="details-toggle" :class="{ expanded: detailsExpanded }" type="button"
        :aria-expanded="detailsExpanded" :aria-controls="detailsId"
        :aria-label="detailsExpanded ? '收起歌手和专辑' : '展开歌手和专辑（可选）'"
        :title="detailsExpanded ? '收起可选信息' : '展开可选信息'" @click="detailsExpanded = !detailsExpanded">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="m5 9 7 7 7-7" />
        </svg>
      </button>
      <Transition name="details">
        <div v-show="detailsExpanded" :id="detailsId" class="optional-fields">
          <label>
            <span>歌手 <small>可选</small></span>
            <input v-model="artist" aria-label="歌手（可选）" placeholder="输入歌手" autocomplete="off" />
          </label>
          <label>
            <span>专辑 <small>可选</small></span>
            <input v-model="album" aria-label="专辑（可选）" placeholder="输入专辑" autocomplete="off" />
          </label>
        </div>
      </Transition>
    </template>
  </form>
</template>

<style scoped>
.search-control {
  --panel: var(--mp-bg);
  --text: var(--mp-text);
  --text-dim: var(--mp-dim);
  --accent: var(--mp-accent);
  --accent-2: var(--mp-accent);
  --border: var(--mp-track);
  color: var(--text);
  color-scheme: light;
  /* 外观调节：搜索框高度、边框厚度、圆点中心间距、收起后的上下间距。 */
  --search-height: 40px;
  --search-border-width: 1px;
  --mode-gap: 28px;
  --rest-mode-gap: 2px;
  width: min(100%, 680px);
  margin: 14px auto 18px;
}
.search-capsule { position: relative; isolation: isolate; display: flex; align-items: center; height: var(--search-height); padding: 0 18px 0 10px; border: var(--search-border-width) solid var(--border); border-radius: 999px; background: var(--panel); transition: border-color .25s, box-shadow .25s; }
.search-capsule::before {
  content: '';
  position: absolute;
  inset: 0;
  z-index: -1;
  border-radius: inherit;
  pointer-events: none;
  background: linear-gradient(110deg,
    color-mix(in srgb, var(--panel) 85%, transparent),
    color-mix(in srgb, var(--accent) 12%, transparent));
  opacity: 0;
  transition: opacity .25s ease;
}
.show-controls .search-capsule::before { opacity: 1; }
.search-capsule:focus-within { border-color: var(--accent); box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 10%, transparent); }
.search-submit { flex: 0 0 min(44px, calc(var(--search-height) - 12px)); display: grid; place-items: center; width: min(44px, calc(var(--search-height) - 12px)); height: min(44px, calc(var(--search-height) - 12px)); padding: 0; border: 0; border-radius: 50%; background: transparent; color: var(--text); }
.search-submit:hover { background: color-mix(in srgb, var(--accent) 12%, transparent); color: var(--accent); transform: none; }
.search-submit:disabled { opacity: .4; cursor: default; transform: none; }
.search-submit svg { display: block; width: 24px; height: 24px; }
.song-field, .link-field { flex: 1; }
.search-capsule input { width: 100%; min-width: 0; padding: 8px 14px; border: 0; border-radius: 0; background: transparent; font-size: 14px; }
.search-capsule input::placeholder { color: var(--text-dim); }
.details-toggle {
  display: grid;
  place-items: center;
  width: 44px;
  height: 36px;
  margin: 4px auto 0;
  padding: 8px;
  border: 0;
  background: transparent;
  color: var(--text-dim);
}
.details-toggle:hover { background: transparent; color: var(--accent); transform: none; }
.details-toggle, .mode-caption, .mode-switch {
  opacity: 0;
  visibility: hidden;
  transition: opacity .2s ease, visibility .2s, height .25s ease, margin .25s ease, padding .25s ease;
}
.search-control:not(.show-controls) .details-toggle { height: 0; margin-top: 0; padding-top: 0; padding-bottom: 0; overflow: hidden; }
.show-controls .details-toggle, .show-controls .mode-caption, .show-controls .mode-switch {
  opacity: 1;
  visibility: visible;
}
.details-toggle svg { width: 22px; height: 22px; transition: transform .25s ease; }
.details-toggle.expanded svg { transform: rotate(180deg); }
.details-toggle:focus-visible { outline: 2px solid var(--accent-2); outline-offset: 2px; }
.optional-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  max-height: 180px;
  padding: 8px 16px 16px;
}
.optional-fields label { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.optional-fields label > span { color: var(--text-dim); font-size: 12px; }
.optional-fields small { margin-left: 4px; opacity: .65; font-size: 11px; }
.optional-fields input { width: 100%; min-width: 0; background: var(--panel); }
.optional-fields { overflow: hidden; transition: max-height .25s ease, opacity .2s ease, padding .25s ease, visibility .25s; }
.search-control:not(.show-controls) .optional-fields {
  max-height: 0;
  padding-top: 0;
  padding-bottom: 0;
  opacity: 0;
  visibility: hidden;
}
.details-enter-active, .details-leave-active { overflow: hidden; transition: max-height .25s ease, opacity .2s ease, padding .25s ease, transform .25s ease; }
.details-enter-from, .details-leave-to { max-height: 0; padding-top: 0; padding-bottom: 0; opacity: 0; transform: translateY(-6px); }
.mode-switch { position: relative; height: 44px; margin-top: 12px; transition: margin-top .25s ease, opacity .2s ease, visibility .2s; }
.search-control:not(.show-controls) .mode-switch { margin-top: var(--rest-mode-gap); }
.mode-button { position: absolute; top: 0; left: calc(50% - var(--mode-gap) / 2); display: grid; place-items: center; width: var(--mode-gap); height: 44px; padding: 0; border: 0; border-radius: 50%; background: transparent; transform: translateX(calc(var(--offset) * var(--mode-gap))); transition: transform .5s cubic-bezier(.22, 1, .36, 1); }
.mode-button:hover { background: transparent; }
.mode-dot { width: 20px; height: 20px; border-radius: 50%; background: var(--text-dim); opacity: .5; transform: scale(.3); transition: transform .5s cubic-bezier(.22, 1, .36, 1), background .3s, opacity .3s, box-shadow .3s; }
.mode-button:hover .mode-dot { opacity: 1; }
.mode-button.selected { transform: translateX(0) translateY(-3px); }
.selected .mode-dot { transform: scale(1); opacity: 1; background: var(--accent); box-shadow: 0 4px 18px color-mix(in srgb, var(--accent) 30%, transparent); }
.mode-button:focus-visible, .search-submit:focus-visible { outline: 2px solid var(--accent-2); outline-offset: 2px; }
.mode-caption { height: 22px; text-align: center; color: var(--text-dim); font-size: 12px; letter-spacing: 1px; }
.mode-caption span { display: inline-block; }
.mode-caption .current-mode { color: var(--accent); }
.caption-enter-active, .caption-leave-active { transition: opacity .15s, transform .15s; }
.caption-enter-from { opacity: 0; transform: translateY(5px); }
.caption-leave-to { opacity: 0; transform: translateY(-5px); }
.search-spinner { flex: 0 0 16px; height: 16px; margin-left: 8px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: spin .8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
@media (max-width: 480px) {
  .search-control { --search-height: 56px; }
  .search-capsule { padding-right: 10px; padding-left: 6px; }
  .search-capsule input { font-size: 12px; padding: 8px; }
  .search-submit { flex-basis: 36px; width: 36px; height: 36px; }
}
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { transition: none !important; animation: none !important; }
}
</style>
