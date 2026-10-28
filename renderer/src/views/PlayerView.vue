<script setup lang="ts">
import { onMounted, ref } from 'vue'
import AudioPlayer from '@/components/AudioPlayer.vue'
import { host } from '@/bridge/host'

const player = ref<InstanceType<typeof AudioPlayer> | null>(null)

const file = ref<File | null>(null)
/** 流式地址（http/https）。file 优先，所以切到流式时要清空 file */
const src = ref('')
const urlInput = ref('')
const status = ref('')
/** 显示标题：代理地址（stream://media/1）本身没有文件名，用原始来源推一个 */
const title = ref('')
/** 歌手/作者（B站 解析结果里带） */
const artist = ref('')
/** B站 解析中（下载 + 转码） */
const biliBusy = ref(false)
const currentTime = ref(0)
const duration = ref(0)
const bufferedCount = ref(0)
const isLoading = ref(false)
/** 媒体元素底层状态，流式排错用 */
const mediaInfo = ref('')

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement | null
  const picked = input?.files?.[0] ?? null
  if (!picked) return
  src.value = ''
  file.value = picked
  title.value = picked.name.replace(/\.[a-z0-9]+$/i, '')
  status.value = `已载入：${picked.name}（${(picked.size / 1024 / 1024).toFixed(1)} MB）`
}

function titleFromUrl(url: string): string {
  const segment = url.split('?')[0].split('/').filter(Boolean).pop() ?? url
  return decodeURIComponent(segment).replace(/\.[a-z0-9]+$/i, '') || url
}

function playUrl(): void {
  const url = urlInput.value.trim()
  if (!url) return
  file.value = null
  src.value = url
  title.value = titleFromUrl(url)
  status.value = `流式播放：${url}`
}

/**
 * 让宿主代理这个地址（stream://）：prod 下页面是 secure origin，
 * 直接加载 http 媒体会被混合内容拦掉，走代理就没这个问题；
 * 以后接 B站这类需要 Referer 的源也走这里。
 */
async function playViaProxy(): Promise<void> {
  const url = urlInput.value.trim()
  if (!url) return
  const proxied = await host.registerStream(url, {})
  if (!proxied || proxied === url) {
    status.value = '宿主未连接（浏览器预览模式），无法使用 stream:// 代理'
    return
  }
  file.value = null
  src.value = proxied
  title.value = titleFromUrl(url)
  status.value = `代理播放：${proxied} ← ${url}`
}

/** B站 地址：宿主解析 + 转码成 mp3（几分钟的视频通常 5~20 秒） */
async function playBilibili(): Promise<void> {
  await playRemote('resolveBilibili', 'B站')
}

/** YouTube 地址：宿主用 yt-dlp 取流 + ffmpeg 转 mp3（首次通常 10~40 秒） */
async function playYouTube(): Promise<void> {
  await playRemote('resolveYouTube', 'YouTube')
}

/** B站 / YouTube 共用流程：解析 -> 取 stream:// 地址 -> 播放 -> 更新标题歌手 */
async function playRemote(kind: 'resolveBilibili' | 'resolveYouTube', label: string): Promise<void> {
  const input = urlInput.value.trim()
  if (!input) return
  biliBusy.value = true
  status.value = `正在解析 ${label} 音频（下载 + 转码 mp3）…`
  try {
    const result =
      kind === 'resolveBilibili' ? await host.resolveBilibili(input) : await host.resolveYouTube(input)
    if (!result.ok || !result.streamUrl) {
      status.value = `${label} 解析失败：${result.error ?? '未知错误'}`
      return
    }

    file.value = null
    src.value = result.streamUrl
    title.value = result.title ?? result.bvid ?? `${label} 音频`
    artist.value = result.artist ?? ''
    status.value = `${label} 播放：${result.title}${result.cached ? '（命中缓存）' : '（已转码）'}`
  } finally {
    biliBusy.value = false
  }
}

/** 生成一段 4 音琶音 WAV，方便没有 mp3 时也能试听 */
function createDemoTone(seconds = 8): File {
  const sampleRate = 44100
  const total = Math.floor(sampleRate * seconds)
  const buffer = new ArrayBuffer(44 + total * 2)
  const view = new DataView(buffer)
  const writeAscii = (offset: number, text: string): void => {
    for (let i = 0; i < text.length; i += 1) view.setUint8(offset + i, text.charCodeAt(i))
  }

  writeAscii(0, 'RIFF')
  view.setUint32(4, 36 + total * 2, true)
  writeAscii(8, 'WAVE')
  writeAscii(12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true)
  view.setUint16(22, 1, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true)
  view.setUint16(32, 2, true)
  view.setUint16(34, 16, true)
  writeAscii(36, 'data')
  view.setUint32(40, total * 2, true)

  const notes = [261.63, 329.63, 392.0, 523.25]
  const noteLength = seconds / notes.length

  for (let i = 0; i < total; i += 1) {
    const t = i / sampleRate
    const noteIndex = Math.min(notes.length - 1, Math.floor(t / noteLength))
    const local = t - noteIndex * noteLength
    const envelope = Math.min(1, local * 30) * Math.exp(-local * 1.6) * Math.min(1, (seconds - t) * 3)
    const sample = Math.sin(2 * Math.PI * notes[noteIndex] * t) * 0.2 * envelope
    view.setInt16(44 + i * 2, Math.round(sample * 0x7fff), true)
  }

  return new File([buffer], 'demo-arpeggio.wav', { type: 'audio/wav' })
}

function useDemo(): void {
  src.value = ''
  file.value = createDemoTone()
  title.value = 'demo-arpeggio'
  status.value = '已生成 8 秒测试音频（C-E-G-C 琶音）'
}

/** 媒体状态每 500ms 刷新一次（加载失败时 timeupdate 会停，靠这个兜住） */
function refreshMediaInfo(): void {
  const el = player.value?.controller.element
  const source = file.value ? `file:${file.value.name}` : src.value ? `src:${src.value.slice(-28)}` : '-'
  mediaInfo.value = el
    ? `${source} | readyState=${el.readyState} networkState=${el.networkState} err=${el.error?.code ?? 0}`
    : `${source} | no element`
}

function onTimeupdate(time: number, total: number): void {
  currentTime.value = time
  duration.value = total
  const snapshot = player.value?.controller.snapshot()
  bufferedCount.value = snapshot?.buffered.length ?? 0
  isLoading.value = snapshot?.loading ?? false
  refreshMediaInfo()
}

onMounted(() => {
  useDemo()
  window.setInterval(refreshMediaInfo, 500)
})
</script>

<template>
  <main class="page">
    <header class="head">
      <h1>音频播放器组件</h1>
      <p>
        只实现了播放/暂停、进度条、音量；封面、标题、歌手来自 <code>file</code> /
        <code>title</code> / <code>artist</code> 属性。播放实现走
        <code>src/playback/</code> 的控制器，本地文件和 http 直链都支持。
      </p>
    </header>

    <section class="card">
      <label class="picker">
        <input type="file" accept="audio/*,.mp3" @change="onFileChange" />
        <span>选择一个本地音频文件（mp3 / wav / m4a 均可）</span>
      </label>
      <div class="row">
        <button type="button" @click="useDemo">换成测试音频</button>
        <button type="button" @click="player?.toggle()">toggle()</button>
        <button type="button" @click="player?.seekTo(4)">seekTo(4s)</button>
        <button type="button" @click="player?.setVolume(0.3)">setVolume(0.3)</button>
      </div>
      <div class="row">
        <input
          v-model="urlInput"
          class="url-input"
          placeholder="流式地址 / B站 视频地址 / BV 号，例：https://www.bilibili.com/video/BV1GJ411x7h7"
          @keyup.enter="playUrl"
        />
        <button class="primary" type="button" @click="playUrl">以流式地址播放</button>
        <button type="button" @click="playViaProxy">走 stream:// 代理播放</button>
        <button type="button" :disabled="biliBusy" @click="playBilibili">
          {{ biliBusy ? '解析中…' : '解析 B站 音频' }}
        </button>
        <button type="button" :disabled="biliBusy" @click="playYouTube">解析 YouTube 音频</button>
      </div>
      <p class="hint">{{ status }}</p>
      <p class="hint">
        进度：{{ currentTime.toFixed(1) }}s / {{ duration.toFixed(1) }}s · 已缓冲区间
        {{ bufferedCount }} 段 · {{ isLoading ? '缓冲中…' : '就绪' }} —— 数据来自
        <code>timeupdate</code> 与 <code>controller.snapshot()</code>。
      </p>
      <p class="hint">媒体状态：<code>{{ mediaInfo }}</code></p>
    </section>

    <div class="dock">
      <AudioPlayer
        ref="player"
        :file="file"
        :src="src"
        :title="title"
        :artist="artist"
        :default-volume="0.8"
        autoplay
        @timeupdate="onTimeupdate"
        @ended="status = '播放结束'"
        @error="status = $event"
      />
    </div>
  </main>
</template>

<style scoped>
.page {
  max-width: 1080px;
  margin: 0 auto;
  padding: 28px 28px 140px;
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.head h1 {
  margin: 0 0 8px;
  font-size: 24px;
}

.head p {
  margin: 0;
  color: var(--text-dim);
  font-size: 13.5px;
  line-height: 1.7;
  user-select: text;
}

.card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 18px 20px;
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
}

.picker {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 13px;
  color: var(--text-dim);
}

.picker input {
  color: var(--text);
  font-size: 12.5px;
}

.row {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.url-input {
  flex: 1;
  min-width: 0;
}

.hint {
  margin: 0;
  font-size: 12.5px;
  color: var(--text-dim);
  user-select: text;
}

.dock {
  position: fixed;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 16px 28px 20px;
  background: linear-gradient(to top, rgba(14, 15, 19, 0.96), rgba(14, 15, 19, 0));
}

.dock > * {
  max-width: 1080px;
  margin: 0 auto;
}
</style>
