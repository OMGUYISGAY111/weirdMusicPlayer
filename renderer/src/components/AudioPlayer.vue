<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

interface Props {
  /** 音频地址：http(s) / app:// / blob: 都可以 */
  src?: string
  /** 用户本地选中的音频文件，优先级高于 src（内部会 createObjectURL，卸载时释放） */
  file?: File | null
  /** 标题，缺省用文件名 */
  title?: string
  /** 歌手/副标题，缺省显示「本地文件」 */
  artist?: string
  /** 封面图地址，缺省显示音符占位 */
  cover?: string
  /** 初始音量 0~1 */
  defaultVolume?: number
  /** 元数据加载完成后自动播放 */
  autoplay?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  src: '',
  file: null,
  title: '',
  artist: '',
  cover: '',
  defaultVolume: 0.8,
  autoplay: false,
})

const emit = defineEmits<{
  play: []
  pause: []
  ended: []
  loaded: [duration: number]
  timeupdate: [currentTime: number, duration: number]
  error: [message: string]
}>()

const audioEl = ref<HTMLAudioElement | null>(null)
const progressEl = ref<HTMLElement | null>(null)

const playing = ref(false)
const currentTime = ref(0)
const duration = ref(0)
const volume = ref(clamp01(props.defaultVolume))
const muted = ref(false)
const dragging = ref(false)
const dragRatio = ref(0)
const volumeExpanded = ref(false)
const errorMessage = ref('')

/** 本地文件 -> 对象 URL，切换文件/卸载时释放，避免内存泄漏 */
const objectUrl = ref('')

function releaseObjectUrl(): void {
  if (!objectUrl.value) return
  URL.revokeObjectURL(objectUrl.value)
  objectUrl.value = ''
}

watch(
  () => props.file,
  (file) => {
    releaseObjectUrl()
    if (file) objectUrl.value = URL.createObjectURL(file)
  },
  { immediate: true },
)

/** file 优先，其次是 src */
const audioSrc = computed(() => objectUrl.value || props.src)

const fallbackTitle = computed(() => {
  const fileName = props.file?.name ?? props.src.split('/').pop() ?? ''
  return fileName.replace(/\.[a-z0-9]+$/i, '') || '未命名音频'
})

const displayTitle = computed(() => props.title || fallbackTitle.value)
const displayArtist = computed(() => props.artist || (props.file ? '本地文件' : ''))

const hasDuration = computed(() => Number.isFinite(duration.value) && duration.value > 0)

/** 拖动中显示手指位置，松手后跟随播放进度 */
const playedRatio = computed(() => {
  if (dragging.value) return dragRatio.value
  if (!hasDuration.value) return 0
  return clamp01(currentTime.value / duration.value)
})

const fillStyle = computed(() => ({ width: `${(playedRatio.value * 100).toFixed(3)}%` }))
// 圆点圆心就是播放比例位置（和填充线末端对齐）
const thumbStyle = computed(() => ({ left: `${(playedRatio.value * 100).toFixed(3)}%` }))

// 圆点两侧的时间标签做成一组跟着圆点走：红＝已播放（数字），灰＝总时长（数字）。
// 半宽用 ResizeObserver 实测，两端按真实宽度夹紧，贴边时不会多缩也不会溢出。
const timeGroupEl = ref<HTMLElement | null>(null)
const timeGroupHalf = ref(52)

const timeGroupStyle = computed(() => ({
  left: `clamp(${timeGroupHalf.value}px, ${(playedRatio.value * 100).toFixed(3)}%, calc(100% - ${timeGroupHalf.value}px))`,
}))

/** 进度条宽度（实测），用于判断竖标被夹住、跟不上圆点的距离 */
const progressWidth = ref(0)

/**
 * 脱开程度 0~1：0 = 竖标正好压在圆点上；1 = 已经被夹到端点。
 * 被夹住期间竖标会「变灰 + 变短」，说明它此刻不代表圆点位置。
 */
const tickDetach = computed(() => {
  const width = progressWidth.value
  const half = timeGroupHalf.value
  if (!width || !half) return 0

  const desired = playedRatio.value * width
  const actual = Math.min(Math.max(desired, half), width - half)
  return Math.min(1, Math.abs(desired - actual) / half)
})

const tickStyle = computed(() => {
  const detach = tickDetach.value
  const width = progressWidth.value
  const half = timeGroupHalf.value
  const desired = playedRatio.value * width

  // 变灰只在「左端」发生；右端保持主题红，只做变短（结尾完全不变色）
  const atRightEnd = width > 0 && desired > width - half
  const colorDetach = atRightEnd ? 0 : detach

  // 长度：默认保持「短」；只有当点与竖线的距离到达阈值（十分靠近）时才切到全长，
  // 切换瞬间由 CSS transition 播放这一段变化动画；离开阈值同样动画回到短。
  const NEAR = 0 // 脱开程度 < 0.2（约 7px 内）算到达阈值
  const atThreshold = detach <= NEAR
  const keep = atThreshold ? 1 : 0.4 // 到达阈值 → 全长, 否则 → 短

  const accentPercent = Math.round((1 - colorDetach) * 100)

  return {
    height: `calc(var(--mp-tick-h) * ${keep.toFixed(3)})`,
    background:
      accentPercent >= 100
        ? 'var(--mp-accent)'
        : `color-mix(in srgb, var(--mp-accent) ${accentPercent}%, var(--mp-dim))`,
  }
})

const effectiveVolume = computed(() => (muted.value ? 0 : volume.value))
const volumeIcon = computed<'mute' | 'low' | 'high'>(() => {
  if (effectiveVolume.value <= 0.001) return 'mute'
  return effectiveVolume.value < 0.5 ? 'low' : 'high'
})
const volumeTrackStyle = computed(() => {
  const percent = `${(effectiveVolume.value * 100).toFixed(1)}%`
  return {
    background: `linear-gradient(to right, var(--mp-accent) 0 ${percent}, var(--mp-track) ${percent} 100%)`,
  }
})

async function play(): Promise<void> {
  const el = audioEl.value
  if (!el || !audioSrc.value) return
  try {
    await el.play()
  } catch (error) {
    // 自动播放被拦、或解码失败
    setError(String(error))
  }
}

function pause(): void {
  audioEl.value?.pause()
}

function toggle(): void {
  if (playing.value) pause()
  else void play()
}

function seekTo(seconds: number): void {
  const el = audioEl.value
  if (!el || !hasDuration.value) return
  el.currentTime = clamp(seconds, 0, duration.value)
  currentTime.value = el.currentTime
}

function seekByRatio(ratio: number): void {
  const el = audioEl.value
  if (!el || !hasDuration.value) return
  el.currentTime = clamp01(ratio) * duration.value
  currentTime.value = el.currentTime
}

function setVolume(next: number): void {
  volume.value = clamp01(next)
  muted.value = false
}

function toggleMute(): void {
  if (muted.value) {
    muted.value = false
    if (volume.value <= 0.001) volume.value = 0.5
    return
  }
  muted.value = true
}

function onVolumeInput(event: Event): void {
  const input = event.target as HTMLInputElement | null
  if (!input) return
  setVolume(input.valueAsNumber)
}

/* ---------- 进度条交互 ---------- */

function ratioFromPointer(event: PointerEvent): number {
  const el = progressEl.value
  if (!el) return 0
  const rect = el.getBoundingClientRect()
  if (rect.width <= 0) return 0
  return clamp01((event.clientX - rect.left) / rect.width)
}

function onProgressPointerDown(event: PointerEvent): void {
  if (!hasDuration.value) return
  dragging.value = true
  dragRatio.value = ratioFromPointer(event)
  safePointerCapture(event.currentTarget, event.pointerId, true)
  seekByRatio(dragRatio.value)
}

function onProgressPointerMove(event: PointerEvent): void {
  if (!dragging.value) return
  dragRatio.value = ratioFromPointer(event)
  seekByRatio(dragRatio.value)
}

function onProgressPointerUp(event: PointerEvent): void {
  if (!dragging.value) return
  dragging.value = false
  seekByRatio(dragRatio.value)
  safePointerCapture(event.currentTarget, event.pointerId, false)
}

/** 合成事件或已释放的指针会抛 NotFoundError，不能让它打断 seek */
function safePointerCapture(target: EventTarget | null, pointerId: number, capture: boolean): void {
  const el = target as HTMLElement | null
  if (!el) return
  try {
    if (capture) el.setPointerCapture(pointerId)
    else el.releasePointerCapture(pointerId)
  } catch {
    // 忽略：不影响拖动逻辑本身
  }
}

function onProgressKeydown(event: KeyboardEvent): void {
  if (!hasDuration.value) return
  const step = event.shiftKey ? 15 : 5
  switch (event.key) {
    case 'ArrowLeft':
    case 'ArrowDown':
      event.preventDefault()
      seekTo(currentTime.value - step)
      break
    case 'ArrowRight':
    case 'ArrowUp':
      event.preventDefault()
      seekTo(currentTime.value + step)
      break
    case 'Home':
      event.preventDefault()
      seekTo(0)
      break
    case 'End':
      event.preventDefault()
      seekTo(duration.value)
      break
    default:
      break
  }
}

/* ---------- <audio> 事件 ---------- */

function onLoadedMetadata(): void {
  const el = audioEl.value
  if (!el) return
  duration.value = Number.isFinite(el.duration) ? el.duration : 0
  currentTime.value = el.currentTime
  el.volume = volume.value
  el.muted = muted.value
  emit('loaded', duration.value)
  if (props.autoplay) void play()
}

function onTimeUpdate(): void {
  const el = audioEl.value
  if (!el) return
  if (!dragging.value) currentTime.value = el.currentTime
  emit('timeupdate', el.currentTime, duration.value)
}

function onPlay(): void {
  playing.value = true
  emit('play')
}

function onPause(): void {
  playing.value = false
  emit('pause')
}

function onEnded(): void {
  playing.value = false
  currentTime.value = duration.value
  emit('ended')
}

function setError(message: string): void {
  errorMessage.value = message
  emit('error', message)
}

function onAudioError(): void {
  const code = audioEl.value?.error?.code
  setError(code ? `音频加载失败（错误码 ${code}）` : '音频加载失败')
}

/* ---------- 绑定 / 解绑 ---------- */

watch(volume, (next) => {
  const el = audioEl.value
  if (el) el.volume = next
})

watch(muted, (next) => {
  const el = audioEl.value
  if (el) el.muted = next
})

// 换源时重置播放状态
watch(audioSrc, () => {
  currentTime.value = 0
  duration.value = 0
  playing.value = false
  errorMessage.value = ''
})

/** 实测时间标签组宽度，供两端夹紧使用 */
let timeGroupObserver: ResizeObserver | null = null

onMounted(() => {
  const group = timeGroupEl.value
  const bar = progressEl.value
  if ((!group && !bar) || typeof ResizeObserver === 'undefined') return

  timeGroupObserver = new ResizeObserver((entries) => {
    for (const entry of entries) {
      const width = entry.contentRect.width
      if (width <= 0) continue
      if (entry.target === bar) progressWidth.value = width
      else timeGroupHalf.value = width / 2
    }
  })

  if (bar) timeGroupObserver.observe(bar)
  if (group) timeGroupObserver.observe(group)
})

onBeforeUnmount(() => {
  timeGroupObserver?.disconnect()
  timeGroupObserver = null

  const el = audioEl.value
  if (el) {
    el.pause()
    el.removeAttribute('src')
    try {
      el.load()
    } catch {
      // 部分环境下卸载时会抛，忽略
    }
  }
  releaseObjectUrl()
})

/* ---------- 工具 ---------- */

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max)
}

function clamp01(value: number): number {
  return clamp(Number.isFinite(value) ? value : 0, 0, 1)
}

function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '0:00'
  const total = Math.floor(seconds)
  const minutes = Math.floor(total / 60)
  const rest = total % 60
  return `${minutes}:${rest.toString().padStart(2, '0')}`
}

defineExpose({ play, pause, toggle, seekTo, setVolume, toggleMute, audioEl, playing, currentTime, duration, volume, muted })
</script>

<template>
  <div class="mp-player" :class="{ 'is-dragging': dragging, 'is-playing': playing }">
    <!-- 进度线；接触时圆点两侧浮出时间：左边红色＝已播放，右边灰色＝总时长 -->
    <div
      ref="progressEl"
      class="mp-progress"
      role="slider"
      tabindex="0"
      :aria-label="`播放进度 ${formatTime(currentTime)} / ${formatTime(duration)}`"
      :aria-valuemin="0"
      :aria-valuemax="Math.round(duration)"
      :aria-valuenow="Math.round(currentTime)"
      :aria-valuetext="`${formatTime(currentTime)} / ${formatTime(duration)}`"
      :aria-disabled="!hasDuration"
      @pointerdown="onProgressPointerDown"
      @pointermove="onProgressPointerMove"
      @pointerup="onProgressPointerUp"
      @pointercancel="onProgressPointerUp"
      @keydown="onProgressKeydown"
    >
      <span ref="timeGroupEl" class="mp-time-group" :style="timeGroupStyle" aria-hidden="true">
        <span class="mp-time mp-time-played">{{ formatTime(currentTime) }}</span>
        <span class="mp-time-gap" :style="tickStyle" />
        <span class="mp-time mp-time-total">{{ formatTime(duration) }}</span>
      </span>

      <div class="mp-progress-track">
        <div class="mp-progress-fill" :style="fillStyle" />
      </div>
      <span class="mp-progress-thumb" :style="thumbStyle" />
    </div>

    <div class="mp-body">
      <!-- 封面 + 标题/歌手 -->
      <div class="mp-meta">
        <div class="mp-cover">
          <img v-if="cover" :src="cover" :alt="displayTitle" draggable="false" />
          <span v-else class="mp-cover-fallback" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="24" height="24">
              <path
                fill="currentColor"
                d="M20 4.2c0-.6-.5-1-1-1-.1 0-.3 0-.4.1l-9 2.7c-.4.1-.6.5-.6.9v8.3a3.4 3.4 0 1 0 1.6 2.9V9.1l7.8-2.3v5.6a3.4 3.4 0 1 0 1.6 2.9V4.2Z"
              />
            </svg>
          </span>
        </div>
        <div class="mp-text">
          <p class="mp-title" :title="displayTitle">{{ displayTitle }}</p>
          <p v-if="displayArtist" class="mp-artist" :title="displayArtist">{{ displayArtist }}</p>
        </div>
      </div>

      <!-- 播放 / 暂停 -->
      <div class="mp-controls">
        <button
          class="mp-play"
          type="button"
          :disabled="!audioSrc"
          :aria-label="playing ? '暂停' : '播放'"
          :title="playing ? '暂停' : '播放'"
          @click="toggle"
        >
          <svg v-if="playing" viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <rect x="6.6" y="5" width="4" height="14" rx="1.6" fill="currentColor" />
            <rect x="13.4" y="5" width="4" height="14" rx="1.6" fill="currentColor" />
          </svg>
          <svg v-else viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path
              fill="currentColor"
              d="M8.6 5.4c0-.9 1-1.4 1.7-.9l9 6.1c.7.4.7 1.4 0 1.8l-9 6.1c-.7.5-1.7 0-1.7-.9V5.4Z"
            />
          </svg>
        </button>
      </div>

      <!-- 音量：悬停展开滑杆 -->
      <div
        class="mp-volume"
        @mouseenter="volumeExpanded = true"
        @mouseleave="volumeExpanded = false"
      >
        <button
          class="mp-icon-btn"
          type="button"
          :aria-label="volumeIcon === 'mute' ? '取消静音' : '静音'"
          :title="volumeIcon === 'mute' ? '取消静音' : '静音'"
          @click="toggleMute"
        >
          <svg v-if="volumeIcon === 'mute'" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
            <path fill="currentColor" d="M4 9.4h3.1L11.6 6v12L7.1 14.6H4a1 1 0 0 1-1-1v-3.2a1 1 0 0 1 1-1Z" />
            <path
              d="m15.6 9.4 4.8 5.2M20.4 9.4l-4.8 5.2"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
            />
          </svg>
          <svg v-else viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
            <path fill="currentColor" d="M4 9.4h3.1L11.6 6v12L7.1 14.6H4a1 1 0 0 1-1-1v-3.2a1 1 0 0 1 1-1Z" />
            <path
              d="M14.8 9.3a3.9 3.9 0 0 1 0 5.4"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
            />
            <path
              v-if="volumeIcon === 'high'"
              d="M17.4 6.9a7.5 7.5 0 0 1 0 10.2"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
            />
          </svg>
        </button>
        <input
          class="mp-volume-range"
          :class="{ 'is-open': volumeExpanded }"
          type="range"
          min="0"
          max="1"
          step="0.01"
          :value="effectiveVolume"
          :style="volumeTrackStyle"
          :aria-label="`音量 ${Math.round(effectiveVolume * 100)}%`"
          @input="onVolumeInput"
        />
      </div>
    </div>

    <audio
      ref="audioEl"
      :src="audioSrc || undefined"
      preload="metadata"
      @loadedmetadata="onLoadedMetadata"
      @durationchange="onLoadedMetadata"
      @timeupdate="onTimeUpdate"
      @play="onPlay"
      @pause="onPause"
      @ended="onEnded"
      @error="onAudioError"
    />

    <p v-if="errorMessage" class="mp-error">{{ errorMessage }}</p>
  </div>
</template>

<style scoped>
.mp-player {
  /* 主题变量：整体换成深色只需覆盖这几个值 */
  --mp-bg: #ffffff;
  --mp-text: #1a1d21;
  --mp-dim: #9298a1;
  --mp-accent: #ff4d5e;
  --mp-track: #e9ebef;

  /* 进度条几何：--mp-track-h 线粗，--mp-thumb-size 圆点直径，
     --mp-progress-hit 点击/拖拽热区高度（只管手感不影响视觉）；
     hover 变粗用 scaleY，不改布局高度，圆点不会跳。 */
  --mp-track-h: 5px;
  --mp-thumb-size: 12px;
  --mp-progress-hit: 20px;
  /* 圆点上方那根红色竖标（垂直于进度条）：粗细 / 长度都在这改 */
  --mp-tick-w: 2px;
  --mp-tick-h: 23px;

  position: relative;
  display: block;
  padding: 10px 22px 12px;
  background: var(--mp-bg);
  border: 0;
  /* 顶部两角直角，底部圆角 */
  border-radius: 0 0 14px 14px;
  box-shadow: 0 10px 30px rgba(15, 20, 30, 0.1);
  color: var(--mp-text);
  user-select: none;
}

/* 圆点两侧的时间：整组贴着圆点，浮在卡片上沿之外，接触时才淡入。
   中间那根红色竖线垂直于进度条、正对圆点，左边红字＝已播放时长，右边灰字＝总时长。 */
.mp-time-group {
  position: absolute;
  bottom: 100%;
  display: flex;
  align-items: flex-end;
  transform: translateX(-50%);
  opacity: 0;
  transition: opacity 0.15s ease;
  pointer-events: none;
  white-space: nowrap;
}

.mp-time {
  /* 文字离进度线留一点空，竖线则一路伸到线上 */
  padding-bottom: 5px;
  font-size: 13px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
}

/* 正对圆点的红色竖标：垂直于进度条，尺寸用 CSS 变量调；
   跟不上圆点（被夹住）时会变灰、变短，颜色和长度都带过渡 */
.mp-time-gap {
  flex: 0 0 auto;
  width: var(--mp-tick-w);
  height: var(--mp-tick-h);
  margin: 0 8px;
  border-radius: 1px;
  background: var(--mp-accent);
  transition: height 0.18s ease, background-color 0.18s ease;
}

.mp-time-played {
  color: var(--mp-accent);
  font-weight: 700;
}

.mp-time-total {
  color: var(--mp-dim);
  font-weight: 700;
}

.mp-progress:hover .mp-time-group,
.mp-progress:focus-visible .mp-time-group,
.mp-player.is-dragging .mp-time-group {
  opacity: 1;
}

.mp-progress {
  /* 绝对定位贴着卡片上沿：线本身就在最上边缘，时间标签则浮到卡片外面 */
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: var(--mp-progress-hit);
  cursor: pointer;
  touch-action: none;
  outline: none;
}

.mp-progress[aria-disabled='true'] {
  cursor: default;
}

.mp-progress-track {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: var(--mp-track-h);
  border-radius: 999px;
  overflow: hidden;
  /* 未播放部分是灰色轨道 */
  background: var(--mp-track);
  /* 变粗只向下长，不会浮到卡片上沿之外 */
  transform-origin: center top;
  transition: transform 0.15s ease;
}

.mp-progress:hover .mp-progress-track,
.mp-progress:focus-visible .mp-progress-track,
.mp-player.is-dragging .mp-progress-track {
  transform: scaleY(1.35);
}

.mp-progress-fill {
  height: 100%;
  background: var(--mp-accent);
  border-radius: inherit;
}

.mp-progress-thumb {
  position: absolute;
  top: calc(var(--mp-track-h) / 2);
  left: 0;
  width: var(--mp-thumb-size);
  height: var(--mp-thumb-size);
  border-radius: 50%;
  background: #fff;
  border: 2px solid var(--mp-accent);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.2);
  transform: translate(-50%, -50%);
  opacity: 0;
  transition: opacity 0.15s ease;
  pointer-events: none;
}

.mp-progress:hover .mp-progress-thumb,
.mp-progress:focus-visible .mp-progress-thumb,
.mp-player.is-dragging .mp-progress-thumb {
  opacity: 1;
}

.mp-body {
  display: flex;
  align-items: center;
  gap: 18px;
  padding-top: 2px;
}

.mp-meta {
  display: flex;
  align-items: center;
  gap: 14px;
  flex: 1 1 0;
  min-width: 0;
}

.mp-cover {
  flex: 0 0 auto;
  width: 56px;
  height: 56px;
  display: grid;
  place-items: center;
  overflow: hidden;
  border-radius: 50%;
  background: #1a1d21;
  color: #fff;
  box-shadow: 0 0 0 3px #fff, 0 0 0 4px rgba(0, 0, 0, 0.14);
}

.mp-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.mp-cover-fallback {
  display: grid;
  place-items: center;
  opacity: 0.85;
}

.mp-player.is-playing .mp-cover img {
  animation: mp-spin 14s linear infinite;
}

.mp-text {
  min-width: 0;
}

.mp-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  line-height: 1.35;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mp-artist {
  margin: 3px 0 0;
  font-size: 13px;
  line-height: 1.3;
  color: var(--mp-dim);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mp-controls {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  justify-content: center;
}

.mp-play {
  width: 52px;
  height: 52px;
  display: grid;
  place-items: center;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: var(--mp-accent);
  color: #fff;
  cursor: pointer;
  box-shadow: 0 8px 20px rgba(255, 77, 94, 0.35);
  transition: transform 0.12s ease, box-shadow 0.12s ease, opacity 0.12s ease;
}

.mp-play:hover:not(:disabled) {
  transform: scale(1.05);
  box-shadow: 0 10px 24px rgba(255, 77, 94, 0.42);
}

.mp-play:active:not(:disabled) {
  transform: scale(0.97);
}

.mp-play:focus-visible {
  outline: 2px solid var(--mp-accent);
  outline-offset: 3px;
}

.mp-play:disabled {
  cursor: not-allowed;
  opacity: 0.45;
  box-shadow: none;
}

.mp-volume {
  flex: 1 1 0;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 4px;
  min-width: 0;
}

.mp-icon-btn {
  width: 36px;
  height: 36px;
  display: grid;
  place-items: center;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: var(--mp-text);
  cursor: pointer;
  transition: background 0.12s ease;
}

.mp-icon-btn:hover {
  background: rgba(0, 0, 0, 0.06);
}

.mp-icon-btn:focus-visible {
  outline: 2px solid var(--mp-accent);
  outline-offset: 2px;
}

.mp-volume-range {
  -webkit-appearance: none;
  appearance: none;
  width: 0;
  height: 4px;
  padding: 0;
  border: none;
  border-radius: 2px;
  opacity: 0;
  cursor: pointer;
  transition: width 0.18s ease, opacity 0.18s ease, margin 0.18s ease;
}

.mp-volume-range.is-open {
  width: 92px;
  margin-right: 8px;
  opacity: 1;
}

.mp-volume-range::-webkit-slider-thumb {
  -webkit-appearance: none;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: #fff;
  border: 2px solid var(--mp-accent);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.2);
  cursor: pointer;
}

.mp-volume-range::-moz-range-thumb {
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: #fff;
  border: 2px solid var(--mp-accent);
  cursor: pointer;
}

.mp-volume-range:focus-visible {
  outline: 2px solid var(--mp-accent);
  outline-offset: 4px;
}

.mp-error {
  margin: 8px 0 0;
  font-size: 12px;
  color: #e5484d;
}

@keyframes mp-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 720px) {
  .mp-player {
    padding: 12px 14px 10px;
  }

  .mp-body {
    gap: 10px;
  }

  .mp-cover {
    width: 46px;
    height: 46px;
  }

  .mp-play {
    width: 46px;
    height: 46px;
  }

  .mp-volume-range.is-open {
    width: 64px;
  }
}
</style>
