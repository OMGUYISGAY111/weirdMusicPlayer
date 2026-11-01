<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { host, type CookieFileStatus, type SettingsSnapshot } from '@/bridge/host'
import { useSettingsStore } from '@/stores/settings'

/**
 * 设置面板（对话框）：目前只有「YouTube 登录（Cookie）」一项。
 *
 * 数据流：SettingsDialog -> host.getSettings() -> settings.json + 环境变量回退。
 * 保存走 host.saveSettings()，宿主校验 Cookie 文件、写 settings.json 并返回新快照，
 * 因此这里不需要自己判断优先级，也不会出现界面显示和实际生效不一致。
 */

type LoginMode = 'none' | 'file' | 'browser'
type MessageKind = 'ok' | 'warn' | 'error'

const MODES = [
  { key: 'none', label: '不配置' },
  { key: 'file', label: 'Cookie 文件' },
  { key: 'browser', label: '浏览器' },
] as const

const BROWSERS = ['edge', 'chrome', 'firefox', 'brave', 'vivaldi', 'opera', 'chromium']

const settings = useSettingsStore()
const snapshot = computed(() => settings.snapshot)
const inHost = computed(() => snapshot.value?.inHost === true)

const mode = ref<LoginMode>('none')
const filePath = ref('')
const browser = ref('')
const mirrorToEnvironment = ref(false)
const saving = ref(false)
const message = ref('')
const messageKind = ref<MessageKind>('ok')
const preview = ref<CookieFileStatus | null>(null)
const card = ref<HTMLElement | null>(null)
let previousFocus: HTMLElement | null = null

/** 打开 / 保存后把宿主的快照灌进表单 */
function fillFrom(data: SettingsSnapshot | null): void {
  if (!data) return
  const savedFile = data.ui.youtubeCookiesFile.trim()
  const savedBrowser = data.ui.youtubeCookiesBrowser.trim()
  mode.value = savedFile ? 'file' : savedBrowser ? 'browser' : 'none'
  filePath.value = savedFile
  browser.value = savedBrowser
  preview.value = data.cookieFile.path ? data.cookieFile : null
}

watch(snapshot, (value) => fillFrom(value), { immediate: true })

const effectiveLabel = computed(() => {
  const effective = snapshot.value?.effective
  if (!effective) return '读取中…'
  const source = effective.source || '设置'
  if (effective.mode === 'file') return `Cookie 文件 · ${effective.youtubeCookiesFile}（来自${source}）`
  if (effective.mode === 'browser') return `浏览器 · ${effective.youtubeCookiesBrowser}（来自${source}）`
  return '未配置，YouTube 会按未登录状态请求'
})

/** 环境变量里另配了值：设置面板会覆盖它，所以要明说 */
const environmentNote = computed(() => {
  const environment = snapshot.value?.environment
  if (!environment) return ''
  const savedFile = snapshot.value?.ui.youtubeCookiesFile ?? ''
  const savedBrowser = snapshot.value?.ui.youtubeCookiesBrowser ?? ''
  if (environment.youtubeCookiesFile && environment.youtubeCookiesFile === savedFile) {
    return '用户环境变量已指向同一个文件，命令行工具可以直接使用。'
  }
  if (environment.youtubeCookiesFile && environment.youtubeCookiesFile !== savedFile) {
    return `环境变量 MUSICPLAYER_YOUTUBE_COOKIES 也指向 ${environment.youtubeCookiesFile}，选择 Cookie 文件后以设置为准。`
  }
  if (environment.youtubeCookiesBrowser && !environment.youtubeCookiesFile && !savedBrowser) {
    return `环境变量 MUSICPLAYER_YOUTUBE_COOKIES_BROWSER = ${environment.youtubeCookiesBrowser}，在设置里选浏览器会覆盖它。`
  }
  return ''
})

const previewState = computed(() => {
  const info = preview.value
  if (!info || !info.path) return null
  const ok = info.exists && info.netscape
  const title = !info.exists ? '找不到文件' : info.netscape ? '文件可用' : '不是 Netscape 格式'
  const detail = info.exists ? `${formatSize(info.size)}${info.modified ? ` · 修改于 ${info.modified}` : ''}` : ''
  return { ok, exists: info.exists, title, detail, note: info.note }
})

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function setMessage(kind: MessageKind, text: string): void {
  messageKind.value = kind
  message.value = text
}

/** 让宿主体检当前输入框里的路径；不保存 */
async function inspect(): Promise<void> {
  const path = filePath.value.trim()
  if (!path) {
    preview.value = null
    return
  }
  preview.value = await host.checkCookiesFile(path)
}

async function pick(): Promise<void> {
  if (saving.value) return
  const result = await host.pickYouTubeCookiesFile()
  if (!result.ok) {
    setMessage('error', result.error || '打开文件选择框失败')
    return
  }
  if (!result.path) return // 用户取消
  mode.value = 'file'
  filePath.value = result.path
  message.value = ''
  await inspect()
}

function clearFile(): void {
  filePath.value = ''
  preview.value = null
  message.value = ''
}

async function save(): Promise<void> {
  if (saving.value || !inHost.value) return
  saving.value = true
  message.value = ''
  try {
    const result = await host.saveSettings({
      youtubeCookiesFile: mode.value === 'file' ? filePath.value.trim() : '',
      youtubeCookiesBrowser: mode.value === 'browser' ? browser.value.trim() : '',
      persistToUserEnvironment: mirrorToEnvironment.value,
    })
    if (!result.ok) {
      setMessage('error', result.error || '保存失败')
      return
    }
    setMessage(result.warning ? 'warn' : 'ok', result.warning || '已保存，下一次解析 YouTube 立即生效，不用重启。')
    await settings.refresh()
  } finally {
    saving.value = false
  }
}

function close(): void {
  settings.hide()
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.preventDefault()
  close()
}

onMounted(() => {
  previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
  window.addEventListener('keydown', onKeydown)
  void nextTick(() => card.value?.focus())
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  previousFocus?.focus()
})
</script>

<template>
  <Teleport to="body">
    <div class="settings-layer" @pointerdown.self="close">
      <section
        ref="card"
        class="settings-card"
        role="dialog"
        aria-modal="true"
        aria-labelledby="settings-title"
        tabindex="-1"
      >
        <header class="settings-head">
          <div>
            <h2 id="settings-title">设置</h2>
            <p>修改后立即生效，不需要重启播放器。</p>
          </div>
          <button type="button" class="settings-close" aria-label="关闭设置" title="关闭设置" @click="close">✕</button>
        </header>

        <div v-if="!snapshot" class="settings-body">
          <p class="settings-state" role="status">{{ settings.loading ? '正在读取设置…' : '读取设置失败' }}</p>
        </div>

        <div v-else class="settings-body">
          <p v-if="!inHost" class="settings-banner warn">浏览器预览模式没有宿主，这里只能看不能存。</p>
          <p v-if="snapshot.loadWarning" class="settings-banner warn">{{ snapshot.loadWarning }}</p>
          <p v-if="settings.error" class="settings-banner error">{{ settings.error }}</p>

          <section class="settings-section">
            <h3>YouTube 登录（Cookie）</h3>
            <p class="settings-hint">
              当 YouTube 提示「Sign in to confirm you’re not a bot」时，在这里提供登录凭据。
              Cookie 文件优先于浏览器；播放器和搜索都会把这份配置传给 yt-dlp。
            </p>

            <div class="mode-picker" role="radiogroup" aria-label="YouTube 登录方式">
              <button
                v-for="option in MODES"
                :key="option.key"
                type="button"
                role="radio"
                :aria-checked="mode === option.key"
                :class="{ current: mode === option.key }"
                @click="mode = option.key"
              >
                {{ option.label }}
              </button>
            </div>

            <div v-if="mode === 'file'" class="settings-field">
              <label for="settings-cookies-file">Cookie 文件路径（Netscape 格式）</label>
              <div class="field-row">
                <input
                  id="settings-cookies-file"
                  v-model="filePath"
                  spellcheck="false"
                  autocomplete="off"
                  placeholder="例如 D:\MusicPlayerData\youtube-cookies.txt"
                  @change="inspect"
                  @blur="inspect"
                />
                <button type="button" :disabled="saving" @click="pick">选择文件…</button>
                <button type="button" :disabled="saving || !filePath" @click="clearFile">清除</button>
              </div>
              <p v-if="previewState" class="cookie-status" :class="previewState.ok ? 'ok' : previewState.exists ? 'warn' : 'error'">
                <strong>{{ previewState.title }}</strong>
                <span v-if="previewState.detail"> · {{ previewState.detail }}</span>
                <span v-if="previewState.note"> · {{ previewState.note }}</span>
              </p>
              <p v-else class="settings-hint">
                支持按官方说明导出的 cookies.txt；Cookie 是登录凭据，不要放进仓库或分享给别人。
              </p>
            </div>

            <div v-else-if="mode === 'browser'" class="settings-field">
              <label for="settings-cookies-browser">已登录的浏览器</label>
              <div class="field-row">
                <input
                  id="settings-cookies-browser"
                  v-model="browser"
                  list="settings-browser-options"
                  spellcheck="false"
                  autocomplete="off"
                  placeholder="edge / chrome / firefox，需要指定配置时写 edge:Default"
                />
                <datalist id="settings-browser-options">
                  <option v-for="name in BROWSERS" :key="name" :value="name" />
                </datalist>
              </div>
              <p class="settings-hint">
                直接读取浏览器里的 Cookie；保存前先完全退出该浏览器，否则可能读不到或被「无法解密」拦住。
              </p>
            </div>

            <p v-else class="settings-hint">不配置时 YouTube 按未登录请求，遇到人机验证会解析失败。</p>

            <p class="settings-effective">
              当前生效：{{ effectiveLabel }}
              <span v-if="environmentNote">{{ environmentNote }}</span>
            </p>

            <label class="settings-check">
              <input v-model="mirrorToEnvironment" type="checkbox" :disabled="!inHost" />
              <span>同时写入当前用户的 <code>MUSICPLAYER_YOUTUBE_COOKIES</code> 环境变量</span>
            </label>
            <p class="settings-hint">
              勾选后命令行工具（例如 <code>tools/media-search-check</code>）和新开的终端也能直接用这个文件；
              播放器自己始终以这里的设置为准，清空 Cookie 文件时会一并删除该变量。
            </p>
          </section>

          <p class="settings-meta">
            设置文件：<code>{{ snapshot.settingsPath || '（浏览器预览没有设置文件）' }}</code><br />
            yt-dlp：<code>{{ snapshot.ytDlp.found ? snapshot.ytDlp.command : '未找到，YouTube 搜索和解析不可用' }}</code>
          </p>
        </div>

        <p v-if="message" class="settings-message" :class="messageKind" role="status">{{ message }}</p>

        <footer v-if="snapshot" class="settings-foot">
          <button type="button" @click="close">关闭</button>
          <button type="button" class="primary" :disabled="saving || !inHost" @click="save">
            {{ saving ? '保存中…' : '保存' }}
          </button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.settings-layer {
  position: fixed;
  inset: 0;
  z-index: 80;
  display: grid;
  place-items: center;
  padding: 20px;
  background: color-mix(in srgb, var(--mp-text) 42%, transparent);
}

.settings-card {
  display: flex;
  flex-direction: column;
  width: min(680px, calc(100vw - 40px));
  max-height: min(86vh, 760px);
  overflow: hidden;
  border: 1px solid var(--mp-border);
  border-radius: 16px;
  background: var(--mp-panel);
  box-shadow: var(--mp-shadow-lg);
  outline: none;
}

.settings-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 18px 22px 8px;
}

.settings-head h2 {
  margin: 0;
  font-size: 18px;
  letter-spacing: 0.2px;
}

.settings-head p {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--mp-dim);
}

.settings-close {
  flex: 0 0 auto;
  width: 30px;
  height: 30px;
  padding: 0;
  display: grid;
  place-items: center;
  font-size: 13px;
  line-height: 1;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--mp-dim);
}

.settings-close:hover {
  border-color: var(--mp-border);
  background: var(--mp-track);
  color: var(--mp-text);
}

/* 中间内容自己滚动，标题和底部按钮始终可见 */
.settings-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 6px 22px 14px;
}

.settings-state {
  margin: 0;
  font-size: 12.5px;
  color: var(--mp-dim);
}

.settings-banner {
  margin: 0;
  padding: 8px 11px;
  border-radius: 10px;
  font-size: 12.5px;
  line-height: 1.6;
  border: 1px solid var(--mp-border);
  background: var(--mp-track);
}

.settings-banner.warn {
  color: var(--mp-warn);
  border-color: color-mix(in srgb, var(--mp-warn) 45%, transparent);
  background: color-mix(in srgb, var(--mp-warn) 10%, transparent);
}

.settings-banner.error {
  color: var(--mp-danger);
  border-color: color-mix(in srgb, var(--mp-danger) 45%, transparent);
  background: color-mix(in srgb, var(--mp-danger) 10%, transparent);
}

.settings-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px 14px;
  border: 1px solid var(--mp-border);
  border-radius: 14px;
  background: color-mix(in srgb, var(--mp-track) 45%, transparent);
}

.settings-section h3 {
  margin: 0;
  font-size: 13.5px;
  font-weight: 600;
  letter-spacing: 0.2px;
}

.settings-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.65;
  color: var(--mp-dim);
  user-select: text;
}

.mode-picker {
  display: flex;
  gap: 4px;
  padding: 3px;
  border-radius: 10px;
  background: var(--mp-track);
}

.mode-picker button {
  flex: 1;
  padding: 6px 10px;
  font-size: 12.5px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--mp-dim);
}

.mode-picker button:hover {
  color: var(--mp-text);
  border-color: transparent;
  transform: none;
}

.mode-picker button.current {
  background: var(--mp-panel);
  color: var(--mp-text);
  box-shadow: var(--mp-shadow-sm);
}

.mode-picker button:focus-visible {
  outline: 2px solid var(--mp-accent);
  outline-offset: -2px;
}

.settings-field {
  display: flex;
  flex-direction: column;
  gap: 7px;
}

.settings-field label {
  font-size: 12px;
  color: var(--mp-dim);
}

.field-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.field-row input {
  flex: 1;
  min-width: 0;
  font-size: 12.5px;
  padding: 7px 10px;
}

.field-row button {
  flex: 0 0 auto;
  padding: 7px 11px;
  font-size: 12.5px;
  white-space: nowrap;
}

.cookie-status {
  margin: 0;
  font-size: 12px;
  line-height: 1.65;
  word-break: break-word;
  user-select: text;
}

.cookie-status.ok {
  color: var(--mp-ok);
}

.cookie-status.warn {
  color: var(--mp-warn);
}

.cookie-status.error {
  color: var(--mp-danger);
}

.settings-effective {
  margin: 0;
  padding: 8px 11px;
  border-radius: 10px;
  background: var(--mp-track);
  font-size: 12px;
  line-height: 1.65;
  color: var(--mp-text);
  word-break: break-all;
  user-select: text;
}

.settings-effective span {
  display: block;
  margin-top: 4px;
  color: var(--mp-warn);
}

.settings-check {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 12.5px;
  line-height: 1.6;
  cursor: pointer;
}

.settings-check input {
  flex: 0 0 auto;
  width: 15px;
  height: 15px;
  margin: 2px 0 0;
  padding: 0;
  accent-color: var(--mp-accent);
}

.settings-meta {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.7;
  color: var(--mp-dim);
  word-break: break-all;
  user-select: text;
}

.settings-meta code {
  color: var(--mp-text);
}

.settings-message {
  margin: 0;
  padding: 0 22px 8px;
  font-size: 12.5px;
  line-height: 1.6;
  word-break: break-word;
}

.settings-message.ok {
  color: var(--mp-ok);
}

.settings-message.warn {
  color: var(--mp-warn);
}

.settings-message.error {
  color: var(--mp-danger);
}

.settings-foot {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 22px 16px;
  border-top: 1px solid var(--mp-border);
  background: var(--mp-panel);
}

.settings-foot button {
  padding: 8px 16px;
  font-size: 13px;
}

@media (max-width: 560px) {
  .field-row {
    flex-wrap: wrap;
  }

  .field-row input {
    flex: 1 0 100%;
  }
}
</style>
