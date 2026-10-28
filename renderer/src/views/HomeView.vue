<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { host, hostBridge, type HostInfo } from '@/bridge/host'
import { useAppStore } from '@/stores/app'

const appStore = useAppStore()

const info = ref<HostInfo>({
  appName: '…',
  hostVersion: '…',
  cefVersion: '…',
  chromiumVersion: '…',
  platform: '…',
  arch: '…',
  dotnetVersion: '…',
  isDev: false,
  userDataPath: '…',
  userDataSource: '…',
})
const echoInput = ref('你好，C# 宿主')
const echoResult = ref('')
const echoPending = ref(false)

const inCef = computed(() => hostBridge.ready)
const statusText = computed(() => (inCef.value ? 'CEF 宿主已连接' : '浏览器预览（未连接宿主）'))
const statusClass = computed(() => (inCef.value ? 'pill ok' : 'pill warn'))

const envRows = computed(() => [
  { label: '应用', value: info.value.appName },
  { label: '宿主版本', value: info.value.hostVersion },
  { label: 'CEF', value: info.value.cefVersion },
  { label: 'Chromium', value: info.value.chromiumVersion },
  { label: '平台', value: `${info.value.platform} / ${info.value.arch}` },
  { label: '.NET', value: info.value.dotnetVersion },
])

async function refresh() {
  info.value = await host.info()
}

async function sendEcho() {
  echoPending.value = true
  try {
    echoResult.value = await host.echo(echoInput.value)
  } finally {
    echoPending.value = false
  }
}

async function zoomOut() {
  await host.setZoomLevel(-0.5)
}

async function zoomReset() {
  await host.setZoomLevel(0)
}

async function zoomIn() {
  await host.setZoomLevel(0.5)
}

onMounted(refresh)
</script>

<template>
  <main class="page">
    <header class="topbar">
      <div class="brand">
        <RouterLink to="/test">打开测试页</RouterLink>
        <RouterLink to="/search">打开搜索页</RouterLink>
        <RouterLink to="/player">打开播放器页</RouterLink>
        <span class="logo">🎵</span>
        <div>
          <h1>音乐播放器</h1>
          <p>CEF + Vue 3 + TypeScript 起手框架</p>
        </div>
      </div>
      <span :class="statusClass">{{ statusText }}</span>
    </header>

    <section class="hero">
      <h2>Hello, CEF!</h2>
      <p>
        页面由 Chromium Embedded Framework 承载，界面用 Vue 3 + TypeScript + Vite 编写，
        通过 <code>window.hostApi</code> 与 C# 宿主双向通信。
      </p>
      <div class="chips">
        <span class="chip">CEF {{ info.cefVersion }}</span>
        <span class="chip">{{ info.isDev ? 'dev（Vite 热更新）' : 'prod（本地 dist）' }}</span>
        <span class="chip">Vue {{ '3.5' }} + Vite</span>
      </div>
    </section>

    <div class="grid">
      <section class="card">
        <header>
          <h3>运行环境</h3>
          <button class="ghost" @click="refresh">刷新</button>
        </header>
        <dl>
          <template v-for="row in envRows" :key="row.label">
            <dt>{{ row.label }}</dt>
            <dd>{{ row.value }}</dd>
          </template>
        </dl>
        <p class="hint path" :title="info.userDataPath">
          数据目录：{{ info.userDataPath }}（来自 {{ info.userDataSource }}）
        </p>
      </section>

      <section class="card">
        <header>
          <h3>进程通信（JS → C# → JS）</h3>
        </header>
        <div class="row">
          <input v-model="echoInput" placeholder="输入一段文字" @keyup.enter="sendEcho" />
          <button class="primary" :disabled="echoPending" @click="sendEcho">
            {{ echoPending ? '发送中…' : '发送' }}
          </button>
        </div>
        <pre class="result">{{ echoResult || '点击“发送”，C# 侧 HostApi.Echo 会把内容加工后回传。' }}</pre>
      </section>

      <section class="card">
        <header>
          <h3>全局状态（Pinia）</h3>
        </header>
        <p class="counter">{{ appStore.playCount }}</p>
        <p class="hint">当前曲目：{{ appStore.currentTrack }}</p>
        <div class="row">
          <button @click="appStore.tick()">播放次数 +1</button>
          <button @click="appStore.setTrack('示例曲目 - Demo Track')">设为示例曲目</button>
        </div>
      </section>

      <section class="card">
        <header>
          <h3>宿主能力</h3>
        </header>
        <div class="row wrap">
          <button @click="host.openDevTools()">开发者工具</button>
          <button @click="host.openUserDataFolder()">打开数据目录</button>
          <button :disabled="!inCef" @click="host.openLogFolder()">打开诊断日志</button>
          <button @click="zoomOut">缩小</button>
          <button @click="zoomReset">100%</button>
          <button @click="zoomIn">放大</button>
        </div>
        <div class="row wrap window-row">
          <button @click="host.minimize()">最小化</button>
          <button @click="host.toggleMaximize()">最大化 / 还原</button>
          <button class="danger" @click="host.closeWindow()">关闭窗口</button>
        </div>
      </section>
    </div>

    <footer class="footer">
      <code>pnpm dev</code> 同时启动 Vite 与 CEF 宿主；<code>pnpm build</code> 打包前端并编译宿主。
    </footer>
  </main>
</template>

<style scoped>
.page {
  max-width: 1080px;
  margin: 0 auto;
  padding: 26px 28px 40px;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;
}

.logo {
  width: 44px;
  height: 44px;
  display: grid;
  place-items: center;
  font-size: 22px;
  border-radius: 13px;
  background: linear-gradient(135deg, rgba(124, 92, 255, 0.9), rgba(38, 208, 206, 0.85));
  box-shadow: 0 10px 26px rgba(124, 92, 255, 0.35);
}

.brand h1 {
  margin: 0;
  font-size: 19px;
  letter-spacing: 0.4px;
}

.brand p {
  margin: 3px 0 0;
  font-size: 12.5px;
  color: var(--text-dim);
}

.pill {
  font-size: 12.5px;
  padding: 6px 12px;
  border-radius: 999px;
  border: 1px solid var(--border);
  white-space: nowrap;
}

.pill.ok {
  color: var(--ok);
  border-color: rgba(61, 220, 151, 0.4);
  background: rgba(61, 220, 151, 0.1);
}

.pill.warn {
  color: var(--warn);
  border-color: rgba(255, 184, 77, 0.4);
  background: rgba(255, 184, 77, 0.1);
}

.hero {
  padding: 26px 28px;
  border-radius: 20px;
  border: 1px solid var(--border);
  background:
    radial-gradient(700px 240px at 88% -40%, rgba(38, 208, 206, 0.28), transparent 70%),
    radial-gradient(600px 260px at 0% -30%, rgba(124, 92, 255, 0.32), transparent 70%),
    var(--panel);
}

.hero h2 {
  margin: 0 0 10px;
  font-size: 34px;
  letter-spacing: -0.5px;
  background: linear-gradient(96deg, #fff, #c9b8ff 55%, #8ef0ee);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.hero p {
  margin: 0;
  max-width: 720px;
  line-height: 1.7;
  color: #c6cbe0;
  font-size: 14px;
  user-select: text;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 16px;
}

.chip {
  font-size: 12px;
  color: #cfd4e8;
  padding: 5px 10px;
  border-radius: 8px;
  background: var(--panel-2);
  border: 1px solid var(--border);
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.card {
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--panel);
  padding: 18px 20px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.card header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.card h3 {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: 0.3px;
  color: #dfe3f2;
}

dl {
  display: grid;
  grid-template-columns: 88px 1fr;
  gap: 9px 12px;
  margin: 0;
  font-size: 13px;
}

dt {
  color: var(--text-dim);
}

dd {
  margin: 0;
  word-break: break-all;
  user-select: text;
}

.hint {
  margin: 0;
  font-size: 12px;
  color: var(--text-dim);
  user-select: text;
}

.path {
  word-break: break-all;
}

.row {
  display: flex;
  gap: 10px;
}

.row.wrap {
  flex-wrap: wrap;
}

.row input {
  flex: 1;
  min-width: 0;
}

.window-row {
  margin-top: 2px;
}

.result {
  margin: 0;
  min-height: 74px;
  padding: 12px 14px;
  border-radius: 12px;
  background: #0f1117;
  border: 1px solid var(--border);
  font-size: 12.5px;
  line-height: 1.6;
  color: #b9e8dc;
  white-space: pre-wrap;
  word-break: break-word;
  user-select: text;
}

.counter {
  margin: 0;
  font-size: 40px;
  font-weight: 700;
  line-height: 1;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

button.ghost {
  padding: 5px 11px;
  font-size: 12px;
  border-radius: 8px;
  background: transparent;
}

button.danger:hover {
  border-color: #ff6b6b;
  color: #ff9c9c;
}

.footer {
  font-size: 12px;
  color: var(--text-dim);
  text-align: center;
  padding-top: 4px;
  user-select: text;
}

@media (max-width: 880px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
