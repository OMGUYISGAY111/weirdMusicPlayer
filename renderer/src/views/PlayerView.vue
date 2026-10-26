<script setup lang="ts">
import { onMounted, ref } from 'vue'
import AudioPlayer from '@/components/AudioPlayer.vue'

const player = ref<InstanceType<typeof AudioPlayer> | null>(null)

const file = ref<File | null>(null)
const status = ref('')
const currentTime = ref(0)
const duration = ref(0)

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement | null
  const picked = input?.files?.[0] ?? null
  if (!picked) return
  file.value = picked
  status.value = `已载入：${picked.name}（${(picked.size / 1024 / 1024).toFixed(1)} MB）`
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
  file.value = createDemoTone()
  status.value = '已生成 8 秒测试音频（C-E-G-C 琶音）'
}

function onTimeupdate(time: number, total: number): void {
  currentTime.value = time
  duration.value = total
}

onMounted(useDemo)
</script>

<template>
  <main class="page">
    <header class="head">
      <h1>音频播放器组件</h1>
      <p>
        只实现了播放/暂停、进度条、音量；封面、标题、歌手来自 <code>file</code> /
        <code>title</code> / <code>artist</code> 属性。
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
      <p class="hint">{{ status }}</p>
      <p class="hint">
        进度：{{ currentTime.toFixed(1) }}s / {{ duration.toFixed(1) }}s —— 这行数据来自组件的
        <code>timeupdate</code> 事件。
      </p>
    </section>

    <div class="dock">
      <AudioPlayer
        ref="player"
        :file="file"
        title=""
        artist=""
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
