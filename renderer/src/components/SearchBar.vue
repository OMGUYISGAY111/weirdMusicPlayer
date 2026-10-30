<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'

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
/** 卡片打开时点外面只关卡片：这次失焦不再把搜索栏一起收起，红点留在原位。 */
let keepControlsOnBlur = false

function onFocusIn(): void {
  showControls.value = true
  keepControlsOnBlur = false
}

function selectSearch(event: MouseEvent): void {
  closePicker()
  showControls.value = true
  if (event.target === event.currentTarget) mainInput.value?.focus()
}

/** 这些地方有自己的点击行为：输入胶囊、模式圆点、展开箭头、歌名/专辑输入框、选择卡片本身。 */
const INTERACTIVE = '.search-capsule, .mode-button, .details-toggle, .optional-fields, .mode-picker'

function isInteractiveTarget(target: Node): boolean {
  const el = target instanceof Element ? target : target.parentElement
  if (!el) return true
  if (el.closest(INTERACTIVE)) return true
  const marker = markerEl.value
  return marker === target || (marker?.contains(target) ?? false)
}

function onOutsidePointerDown(event: PointerEvent): void {
  const target = event.target
  const form = searchForm.value
  if (!(target instanceof Node) || !form) return
  // 点卡片、红点、胶囊、圆点、箭头或输入框：交给它们自己的点击处理。
  if (isInteractiveTarget(target)) return
  if (pickerOpen.value) {
    // 卡片展开时点外面的空白：只收起卡片，搜索栏保持展开（红点回到原位，和图2 一致）。
    keepControlsOnBlur = true
    hoveredIndex.value = null
    closePicker()
    return
  }
  // 卡片收起后点空白处（搜索栏里的空白也算）：连搜索栏一起收起，红点淡出。
  showControls.value = false
  hoveredIndex.value = null
  pickerOpen.value = false
  const focused = document.activeElement
  if (focused instanceof HTMLElement && form.contains(focused)) focused.blur()
}

function onFocusOut(): void {
  queueMicrotask(() => {
    if (!searchForm.value?.contains(document.activeElement)) {
      if (keepControlsOnBlur) {
        keepControlsOnBlur = false
        return
      }
      showControls.value = false
      hoveredIndex.value = null
      pickerOpen.value = false
    }
  })
}

/**
 * 选模式卡片：点中间的红点后，卡片以红点为原点展开，
 * 红点飞进卡片停在「当前模式」那一行；选好模式后卡片收起、红点飞回搜索栏中心。
 */
const pickerOpen = ref(false)
const pickerOptions = ref<HTMLButtonElement[]>([])
const pickerSlots = ref<HTMLElement[]>([])
const pickerId = useId()

interface Point { x: number; y: number }

/** 红点的位置（相对 .search-control）：收起时在搜索栏中心，展开时在当前模式那一行。 */
const markerPoint = ref<Point>({ x: 0, y: 0 })
const markerVisible = ref(false)
/** 红点已经落到搜索栏中心：此时让下面的圆点在红点底下淡入，再隐藏红点，全程只有一个红点。 */
const markerLanded = ref(false)
const markerEl = ref<HTMLElement | null>(null)
const markerStyle = computed(() => ({
  transform: `translate(${markerPoint.value.x}px, ${markerPoint.value.y}px)`,
}))

/** offsetLeft/offsetTop 不受 transform 影响，卡片缩放动画期间也能量到准确槽位。 */
function offsetInForm(el: HTMLElement | null): Point | null {
  const form = searchForm.value
  if (!form || !el) return null
  let x = 0
  let y = 0
  let node: HTMLElement | null = el
  while (node && node !== form) {
    const parent = node.offsetParent as HTMLElement | null
    x += node.offsetLeft
    y += node.offsetTop
    // 子元素的 offsetLeft 相对 offsetParent 的 padding 边，卡片自己的边框要补回来。
    if (parent && parent !== form) {
      x += parent.clientLeft
      y += parent.clientTop
    }
    node = parent
  }
  return node === form ? { x: x + el.offsetWidth / 2, y: y + el.offsetHeight / 2 } : null
}

/** 收起状态：红点停在搜索栏中间（模式行高 44、上边距 12，中心在胶囊下方 34px）。 */
function barMarkerPoint(): Point | null {
  const form = searchForm.value
  const capsule = form?.querySelector<HTMLElement>('.search-capsule')
  if (!form || !capsule) return null
  return { x: form.clientWidth / 2, y: capsule.getBoundingClientRect().height + 34 }
}

function slotMarkerPoint(index: number): Point | null {
  return offsetInForm(pickerSlots.value[index] ?? null)
}

function moveMarker(target: Point | null): void {
  if (target) markerPoint.value = target
}

let landTimer: number | undefined
let hideMarkerTimer: number | undefined

function clearMarkerTimers(): void {
  if (landTimer !== undefined) {
    window.clearTimeout(landTimer)
    landTimer = undefined
  }
  if (hideMarkerTimer !== undefined) {
    window.clearTimeout(hideMarkerTimer)
    hideMarkerTimer = undefined
  }
}

/**
 * 红点飞回搜索栏后：先让下面的圆点在红点底下淡入（此时红点还盖着它），
 * 等圆点完全显出来再把红点隐藏，整个过程只有一个红点。
 */
function scheduleMarkerHandoff(): void {
  clearMarkerTimers()
  landTimer = window.setTimeout(() => {
    landTimer = undefined
    markerLanded.value = true
    hideMarkerTimer = window.setTimeout(() => {
      hideMarkerTimer = undefined
      markerVisible.value = false
    }, 300)
  }, 430)
}

async function openPicker(): Promise<void> {
  clearMarkerTimers()
  markerLanded.value = false
  hoveredIndex.value = null
  showControls.value = true
  // 先在隐藏状态下把红点摆到搜索栏中心（此时没有过渡），再显示并飞向卡片。
  const barPoint = barMarkerPoint()
  markerPoint.value = barPoint ?? markerPoint.value
  await nextTick()
  // 强制一次样式计算：否则「落位」和「飞向卡片」会被合并到同一帧，红点会从左上角斜着飞过去。
  markerEl.value?.getBoundingClientRect()
  markerVisible.value = true
  pickerOpen.value = true
  await nextTick()
  moveMarker(slotMarkerPoint(activeIndex.value))
  pickerOptions.value[Math.max(activeIndex.value, 0)]?.focus()
}

function closePicker(restoreFocus = false): void {
  if (!pickerOpen.value) return
  pickerOpen.value = false
  markerLanded.value = false
  hoveredIndex.value = null
  nextTick(() => moveMarker(barMarkerPoint()))
  // 立刻把焦点交还给搜索栏：等卡片卸载再聚焦，焦点会掉到 body，整个搜索区会被 focusout 收起。
  if (restoreFocus) modeButtons.value[Math.max(activeIndex.value, 0)]?.focus()
  scheduleMarkerHandoff()
}

/**
 * 红点本身也是按钮：卡片展开时点它 = 点当前模式（收起卡片，回到搜索栏展开态）；
 * 卡片收起时点它 = 展开卡片。
 */
function onMarkerClick(): void {
  if (pickerOpen.value) {
    chooseMode(mode.value)
    return
  }
  void openPicker()
}

/**
 * 当前模式的红点：点一下展开 / 收起选模式卡片；
 * 未选中的小圆点保持原来的「直接切换」快捷方式。
 */
function onModeClick(key: 'song' | 'link'): void {
  if (key !== mode.value) {
    mode.value = key
    if (pickerOpen.value) nextTick(() => moveMarker(slotMarkerPoint(activeIndex.value)))
    return
  }
  if (pickerOpen.value) closePicker(true)
  else openPicker()
}

function chooseMode(key: 'song' | 'link'): void {
  mode.value = key
  closePicker(true)
}

function onPickerKey(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closePicker(true)
    return
  }
  if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const last = modes.length - 1
  const index = event.key === 'Home' ? 0
    : event.key === 'End' ? last
      : event.key === 'ArrowUp' || event.key === 'ArrowLeft'
        ? (activeIndex.value - 1 + modes.length) % modes.length
        : (activeIndex.value + 1) % modes.length
  mode.value = modes[index]!.key
  nextTick(() => {
    moveMarker(slotMarkerPoint(index))
    pickerOptions.value[index]?.focus()
  })
}

/** 搜索栏收起时把红点一起收掉；搜索栏里的圆点由各模式自己的圆点负责显示。 */
watch(showControls, (visible) => {
  if (!visible) {
    pickerOpen.value = false
    markerVisible.value = false
    markerLanded.value = false
    clearMarkerTimers()
  }
})

/** 窗口缩放后卡片宽度 / 圆点位置都会变，重新贴一下。 */
function syncMarker(): void {
  if (!markerVisible.value) return
  moveMarker(pickerOpen.value ? slotMarkerPoint(activeIndex.value) : barMarkerPoint())
}

onMounted(() => {
  document.addEventListener('pointerdown', onOutsidePointerDown, true)
  window.addEventListener('resize', syncMarker)
})
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onOutsidePointerDown, true)
  window.removeEventListener('resize', syncMarker)
  clearMarkerTimers()
})

function moveMode(event: KeyboardEvent): void {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const index = event.key === 'Home' ? 0 : event.key === 'End' ? modes.length - 1 : (activeIndex.value + 1) % modes.length
  mode.value = modes[index]!.key
  modeButtons.value[index]?.focus()
}
</script>

<template>
  <form ref="searchForm" class="search-control" :class="{ 'show-controls': showControls, 'picker-open': pickerOpen, 'marker-visible': markerVisible && !markerLanded }" role="search"
    @focusin="onFocusIn" @focusout="onFocusOut" @submit.prevent="$emit('submit')"
    @keydown.esc="pickerOpen && closePicker(true)">
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
        :aria-checked="mode === item.key" :aria-label="item.label"
        :title="mode === item.key ? `${item.label}（点击选择模式）` : item.label"
        :tabindex="mode === item.key ? 0 : -1"
        :aria-haspopup="mode === item.key ? 'dialog' : undefined"
        :aria-expanded="mode === item.key ? pickerOpen : undefined"
        :aria-controls="mode === item.key ? pickerId : undefined"
        :style="{ '--offset': index - activeIndex }"
        @mouseenter="hoveredIndex = index" @mouseleave="hoveredIndex = null"
        @click="onModeClick(item.key)">
        <span class="mode-dot">
          <svg class="mode-dot-icon" :class="{ 'is-concealed': mode !== item.key || pickerOpen }" viewBox="0 0 24 24" focusable="false">
            <circle cx="5.4" cy="12" r="2.3" />
            <circle cx="12" cy="12" r="2.3" />
            <circle cx="18.6" cy="12" r="2.3" />
          </svg>
        </span>
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

    <Transition name="picker">
      <div v-if="pickerOpen" :id="pickerId" class="mode-picker" role="dialog"
        aria-label="选择搜索模式" @keydown="onPickerKey" @mousedown.prevent @click.self="closePicker(true)">
        <ul class="picker-list" role="radiogroup" aria-label="搜索模式">
          <li v-for="(item, index) in modes" :key="item.key">
            <button type="button" class="picker-option" :class="{ current: mode === item.key }" role="radio"
              :aria-checked="mode === item.key" :tabindex="mode === item.key ? 0 : -1"
              :ref="(el) => { if (el) pickerOptions[index] = el as HTMLButtonElement }"
              @mouseenter="hoveredIndex = index" @mouseleave="hoveredIndex = null"
              @click="chooseMode(item.key)">
              <span class="picker-slot" aria-hidden="true"
                :ref="(el) => { if (el) pickerSlots[index] = el as HTMLElement }" />
              <span class="picker-label">{{ item.label }}</span>
            </button>
          </li>
        </ul>
        <p class="picker-foot">
          <span :class="{ 'is-current': captionMode.key === mode }">{{ captionMode.label }}</span>
        </p>
      </div>
    </Transition>

    <span ref="markerEl" class="mode-marker" :class="{ 'is-hidden': !markerVisible }" :style="markerStyle" aria-hidden="true"
      @mousedown.prevent @click="onMarkerClick">
      <svg class="marker-icon" :class="{ 'is-concealed': pickerOpen }" viewBox="0 0 24 24" focusable="false">
        <circle cx="5.4" cy="12" r="2.3" />
        <circle cx="12" cy="12" r="2.3" />
        <circle cx="18.6" cy="12" r="2.3" />
      </svg>
    </span>
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
  position: relative;
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
.mode-dot { display: grid; place-items: center; width: 20px; height: 20px; border-radius: 50%; background: var(--text-dim); opacity: .5; transform: scale(.3); transition: transform .5s cubic-bezier(.22, 1, .36, 1), background .3s, opacity .3s, box-shadow .3s; }
.mode-button:hover .mode-dot { opacity: 1; }
.mode-button.selected { transform: translateX(0); }
.selected .mode-dot { transform: scale(1); opacity: 1; background: var(--accent); box-shadow: 0 4px 18px color-mix(in srgb, var(--accent) 30%, transparent); }
/* 选中的红点里放省略号；切模式时红点变灰、灰点变红，圆点随按钮滑动。 */
.mode-dot-icon { width: 14px; height: 14px; fill: #fff; transition: opacity .15s ease; }
.mode-dot-icon.is-concealed { opacity: 0; }
.mode-button:focus-visible { outline: none; }
.mode-button:focus-visible .mode-dot { box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 22%, transparent), 0 4px 18px color-mix(in srgb, var(--accent) 30%, transparent); }
.search-submit:focus-visible { outline: 2px solid var(--accent-2); outline-offset: 2px; }
.mode-caption { height: 22px; text-align: center; color: var(--text-dim); font-size: 12px; letter-spacing: 1px; }
.mode-caption span { display: inline-block; }
.mode-caption .current-mode { color: var(--accent); }
.caption-enter-active, .caption-leave-active { transition: opacity .15s, transform .15s; }
.caption-enter-from { opacity: 0; transform: translateY(5px); }
.caption-leave-to { opacity: 0; transform: translateY(-5px); }
.search-spinner { flex: 0 0 16px; height: 16px; margin-left: 8px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: spin .8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
/* 点中间的红点展开的选模式卡片：深色卡片以红点为原点展开（红点由 .mode-marker 单独绘制）。 */
.mode-picker {
  --pk-bg: #1b1e27;
  --pk-border: #3b4256;
  --pk-text: #e8eaf2;
  --pk-dim: #9aa3b8;
  --pk-accent: var(--mp-accent);
  position: absolute;
  top: calc(var(--search-height) + 34px);
  right: 0;
  left: 0;
  z-index: 40;
  width: min(340px, calc(100% - 8px));
  margin: 0 auto;
  padding: 8px;
  border: 1px solid var(--pk-border);
  border-radius: 16px;
  background: var(--pk-bg);
  color: var(--pk-text);
  color-scheme: dark;
  box-shadow: 0 24px 60px rgba(10, 12, 18, .45), 0 2px 10px rgba(10, 12, 18, .3);
  transform-origin: 50% 0;
}
/* 红点：收起时在搜索栏中心，展开时飞到卡片里「当前模式」那一行的槽位。 */
.mode-marker {
  position: absolute;
  top: 0;
  left: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  margin: -10px 0 0 -10px;
  border-radius: 50%;
  background: var(--accent);
  box-shadow: 0 4px 18px color-mix(in srgb, var(--accent) 35%, transparent);
  cursor: pointer;
  transition: transform .42s cubic-bezier(.22, 1, .36, 1), opacity .18s ease;
}
.mode-marker.is-hidden { opacity: 0; pointer-events: none; transition: none; }
/* 卡片展开时搜索栏里的圆点让位给飞到卡片里的红点。 */
.picker-open .mode-switch .mode-dot { opacity: 0; transition-duration: .14s; }
/* 红点在搜索栏上方时，当前模式的圆点先藏起来，避免同时出现两颗红点。 */
.marker-visible .mode-button.selected .mode-dot { opacity: 0; transition-duration: .14s; }
/* 红点里的省略号：卡片展开（红点在卡片里）时隐藏，飞回搜索栏时与下面的圆点对齐。 */
.marker-icon { width: 14px; height: 14px; fill: #fff; transition: opacity .12s ease; }
.marker-icon.is-concealed { opacity: 0; }
.picker-list { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; }
.picker-option { display: flex; align-items: center; gap: 12px; width: 100%; padding: 9px 12px; border: 0; border-radius: 10px; background: transparent; color: var(--pk-dim); font-size: 13px; line-height: 1.45; text-align: left; }
.picker-option:hover { background: rgba(255, 255, 255, .06); color: var(--pk-text); transform: none; }
.picker-option:focus-visible { outline: 2px solid var(--pk-accent); outline-offset: -2px; }
.picker-option.current { color: var(--pk-accent); }
/* 卡片里只有红点一个圆点，未选中的行只留 20px 的槽位给红点落脚。 */
.picker-slot { position: relative; flex: 0 0 20px; width: 20px; height: 20px; }
.picker-slot::before { content: ''; position: absolute; inset: 6px; border: 1.5px solid currentColor; border-radius: 50%; opacity: .8; }
.picker-label { flex: 1; min-width: 0; }
/* 底部这行跟随鼠标所在的模式：当前模式红色，其它模式灰色。 */
.picker-foot { display: flex; justify-content: center; margin: 8px 2px 2px; padding-top: 9px; border-top: 1px solid rgba(255, 255, 255, .08); color: var(--pk-dim); font-size: 12.5px; letter-spacing: .5px; }
.picker-foot .is-current { color: var(--pk-accent); }
.picker-enter-active { transition: opacity .16s ease, transform .26s cubic-bezier(.22, 1, .36, 1); }
.picker-leave-active { transition: opacity .14s ease, transform .18s ease-in; }
.picker-enter-from, .picker-leave-to { opacity: 0; transform: scale(.94); }
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
