# 音乐播放器 · app端（CEF + TypeScript + Vue 3）

一句话：**CEF 内核（CefSharp 154 / Chromium 154）当容器，Vue 3 + TypeScript + Vite 写界面，C# WinForms 做宿主窗口和本地能力。**

![Hello CEF](docs/screenshot.png)

## 目录结构

```
app端/
├─ renderer/                    # 前端：Vue 3 + TS + Vite
│  ├─ index.html               # 带 CSP
│  ├─ vite.config.ts           # base:'./'，为自定义协议/相对路径准备
│  └─ src/
│     ├─ main.ts               # 挂载前先完成宿主对象绑定
│     ├─ App.vue
│     ├─ router/index.ts       # hash 路由（app:// 与 file:// 下都可靠）
│     ├─ stores/app.ts         # Pinia 示例 store
│     ├─ bridge/host.ts        # ★ 前端 ↔ C# 桥（类型 + 降级）
│     └─ views/HomeView.vue    # Hello World 演示页
├─ host/                       # 宿主：C# / WinForms / CefSharp
│  ├─ Program.cs               # CEF 初始化、自定义 app:// 协议注册
│  ├─ MainForm.cs              # 主窗口 + 注册 hostApi
│  ├─ HostApi.cs               # ★ 暴露给 JS 的 C# 方法
│  ├─ WebTarget.cs             # dev server / 本地 dist 的选择逻辑
│  ├─ StartupOptions.cs        # --dev / --url / --web-root
│  └─ app.manifest             # PerMonitorV2 DPI
├─ package.json                # 一键脚本
└─ pnpm-workspace.yaml
```

## 环境要求

- Windows 10/11 x64
- **.NET 8 SDK**（含 WindowsDesktop 运行时）
- **Node 20+ / pnpm 10**
- VC++ 2015–2022 运行时（CefSharp 依赖，一般机器已带）

CEF 二进制由 NuGet 包 `CefSharp.WinForms.NETCore` 自带（约 300MB），**不需要自己下载或编译 CEF，也不需要 Visual Studio / MSVC**。

## 常用命令

| 命令 | 作用 |
| --- | --- |
| `pnpm install` | 装前端依赖（宿主依赖由 `dotnet restore` 自动拉取） |
| `pnpm dev` | 同时起 Vite + CEF 宿主，改前端代码立即热更新 |
| `pnpm dev:web` | 只起 Vite，用浏览器预览页面（此时 `hostApi` 走降级模拟值） |
| `pnpm build` | 打包前端 + 编译宿主（Release） |
| `pnpm start` | 直接跑宿主，加载本地 `renderer/dist`，不需要 dev server |
| `pnpm package` | 产出可分发目录 `dist/` |
| `pnpm typecheck` | 只做 Vue/TS 类型检查 |
| `pnpm clean` | 清理构建产物 |

## 页面是怎么被加载的

宿主有两种加载方式，由 `host/WebTarget.cs` 决定：

1. **dev**：`--dev` 且 `http://127.0.0.1:5173` 可访问 → 直接打开 Vite dev server，热更新可用；同时在 dev 模式开放 `9222` 远程调试端口（Chrome 打开 `chrome://inspect` 可连）。9222 常被本机 Chrome 占用，被占用时宿主会自动关闭该端口，可用 `--debug-port <端口>` 换一个（如 `--debug-port 9333`）。
   - 走代理时加 `--proxy http://127.0.0.1:7897`：CEF 会用这个代理，并自动 bypass `127.0.0.1`/`localhost`（不影响 `app://`、dev server 和 `stream://` 本地文件）。不传时 CEF 用系统代理，yt-dlp 会自动从注册表读同一个代理。
2. **prod**：通过自定义标准协议 **`app://web/index.html`** 读取 `renderer/dist`（`FolderSchemeHandlerFactory`）。

> 为什么不用 `file://`？`file://` 下 ES module 会被 CORS 拦截，页面会白屏。自定义协议既解决这个问题，又能拿到 secure context，后续加本地媒体协议也照这个套路。

查找顺序：`--url` → dev server → 输出目录 `web/` → 从输出目录向上找仓库里的 `renderer/dist` → 当前工作目录。

## 前后端通信（核心）

CefSharp 154 起绑定是**异步**的，必须显式绑定，且方法返回 Promise：

```csharp
// host/MainForm.cs
_browser.JavascriptObjectRepository.Register("hostApi", new HostApi(this), BindingOptions.DefaultBinder);
```

```ts
// renderer/src/bridge/host.ts
await CefSharp.BindObjectAsync('hostApi')   // 之后 window.hostApi 才存在
const raw = await window.hostApi.getHostInfo()
```

约定：

- 方法名自动 PascalCase → camelCase：`GetHostInfo` → `getHostInfo`。
- 复杂结构统一返回 JSON 字符串，前端 `JSON.parse`，避免跨进程类型映射的坑。
- 浏览器里（`pnpm dev:web`）没有宿主，桥接层自动降级成模拟值，页面照常开发。
- UI 相关调用必须切到 WinForms 线程（`HostApi.OnUiThread`）。

已有方法：`getHostInfo` / `echo` / `minimize` / `toggleMaximize` / `closeWindow` / `openDevTools` / `setZoomLevel` / `openUserDataFolder`。

### 新增一个宿主能力

1. `host/HostApi.cs` 加一个 `public` 方法（改 UI 的用 `OnUiThread` 封送）。
2. `renderer/src/bridge/host.ts`：在 `CefHostApi` 接口里加同名 camelCase 方法，并在 `host` 对象上加一层 Promise 包装。
3. 页面里 `await host.xxx()`。

### 新增一个页面

在 `renderer/src/views/` 新建 `XxxView.vue`，然后到 `renderer/src/router/index.ts` 的 `routes` 里注册即可（**保持 hash 路由**，`app://` 与 `file://` 下都可靠）。

## 音频播放器组件（`AudioPlayer.vue`）

[`renderer/src/components/AudioPlayer.vue`](renderer/src/components/AudioPlayer.vue)：按设计稿做的播放条，只实现**播放/暂停、进度条、音量**三件事；封面/标题/歌手只是展示属性。

![播放器组件](docs/player.png)

演示页在 `/player`（`PlayerView.vue`）：可以选本地文件，也可以一键生成测试音频。

```vue
<script setup lang="ts">
import { ref } from 'vue'
import AudioPlayer from '@/components/AudioPlayer.vue'

const file = ref<File | null>(null)

function onPick(event: Event) {
  const input = event.target as HTMLInputElement
  file.value = input.files?.[0] ?? null
}
</script>

<template>
  <input type="file" accept="audio/*,.mp3" @change="onPick" />
  <AudioPlayer :file="file" :default-volume="0.8" @ended="下一首" />
</template>
```

| Props | 类型 | 说明 |
| --- | --- | --- |
| `file` | `File \| null` | 本地选中的音频，优先于 `src`；内部 `createObjectURL`，卸载时自动释放 |
| `src` | `string` | 音频地址（`app://` / `blob:` / `http(s)` 都行） |
| `title` / `artist` | `string` | 不传时标题取文件名、副标题显示「本地文件」 |
| `cover` | `string` | 封面图；不传显示音符占位 |
| `defaultVolume` | `number` | 初始音量 0~1，默认 0.8 |
| `autoplay` | `boolean` | 元数据就绪后自动播放 |

| 事件 | 参数 | 说明 |
| --- | --- | --- |
| `play` / `pause` / `ended` | — | 播放状态变化 |
| `loaded` | `duration` | 元数据加载完成 |
| `timeupdate` | `currentTime, duration` | 进度变化（约 4 次/秒） |
| `error` | `message` | 解码/加载失败 |

对外方法（通过 `ref` 调用）：`play()`、`pause()`、`toggle()`、`seekTo(seconds)`、`setVolume(0~1)`、`toggleMute()`。

换深色主题只覆盖 CSS 变量即可：

```css
.audio-player-dark :deep(.mp-player) {
  --mp-bg: #1b1e27;
  --mp-text: #e8eaf2;
  --mp-dim: #9aa3b8;
  --mp-track: #2b3040;
}
```

进度线**紧贴卡片上沿**（顶部两角是直角，没有白边）。已播放段是实心主题红，未播放段是灰色轨道（`--mp-track`）。

**接触时**（hover / 拖动 / 键盘聚焦）圆点上方会浮出一个**垂直于进度条的红色竖标**，竖标两侧是时间：左边红色数字 = 已播放时长、右边灰色数字 = 总时长（13px 粗体）。竖标与圆点严格同心（实测中心 x 完全一致），整组跟着圆点移动；贴近两端时按实测宽度整体夹紧，既不会溢出，也不会两个标签互相压住。

几何都能只用变量调（覆盖在 `.mp-player` 上）：

| 变量 | 默认 | 作用 |
| --- | --- | --- |
| `--mp-track-h` | `5px` | 进度线粗细（hover 用 `scaleY` 变粗，不改布局高度，圆点不跳） |
| `--mp-thumb-size` | `12px` | 圆点直径 |
| `--mp-progress-hit` | `20px` | 点击/拖拽热区高度（只管手感，不影响视觉） |
| `--mp-tick-w` | `2px` | 圆点上方红色竖标的粗细 |
| `--mp-tick-h` | `23px` | 圆点上方红色竖标的长度（调大只会向上变长，不影响布局） |

> 竖标的长度是**阈值切换**，不做连续渐变：
>
> - 点与竖线相距 ≤ 阈值（默认约 7px，即脱开程度 < 0.2）→ **全长**；
> - 超过阈值（已经脱开）→ **固定的短**（约为 `--mp-tick-h` 的 40%）；
> - 跨过阈值的那一下由 `transition: height .18s` 播放成动画，中间不会出现任何中间长度；
> - 颜色：**左侧已播放数字**恒为主题红（左端竖标会从灰渐变回红）；**右侧总时长数字**的颜色随整首歌的播放进度**从灰渐变到红**（`color-mix` 按播放百分比混合，配 `transition: color .25s linear`），放到结尾时两边都是红色。

例：想把竖标做得更醒目：

```vue
<AudioPlayer :file="file" style="--mp-tick-h: 26px; --mp-tick-w: 3px" />
```

想让时间**常显**（不只在接触时）就把这段删掉或改成 `opacity: 1`：

```css
/* AudioPlayer.vue 里 .mp-time-group 默认 opacity: 0 */
.mp-time-group { opacity: 1; }
```

**进度交互**：整条进度线都可点、可拖（热区 20px 高），拖动时边拖边听；键盘支持 `←/→`（±5s，Shift ±15s）、`Home/End`；圆点在 hover / 拖动 / 聚焦时才出现，避免平时干扰视觉。

**内核编解码实测**（CEF 154 / Chromium 154）：`mp3` ✅、`wav` ✅、`ogg/opus` ✅、`flac` ✅；`m4a` / `aac` ❌（`canPlayType` 返回空）—— 这是 CEF 官方构建不含 AAC 专有编解码器导致的，要支持 m4a 得换带 proprietary codecs 的 CEF 构建。

### 播放实现分层（`renderer/src/playback/`）

组件不再直接操作 `<audio>`，媒体能力被抽到控制器接口后面，接新的播放形态（HTTP 直链 / HLS / YouTube）时**只新增实现，不动 UI**：

```
renderer/src/playback/
├─ types.ts                    # PlaybackController / PlaybackSnapshot / PlaybackCapabilities
└─ mediaElementController.ts   # 基于 <audio> 的实现（本地文件 / HTTP 直链 / app://）
```

| 契约 | 说明 |
| --- | --- |
| `load({ file, url })` | `file` 优先（内部 `createObjectURL`，自动释放）；否则用 `url`（http/https/app:// 都行） |
| `play / pause / seek / setVolume / setMuted` | 统一操作入口 |
| `snapshot()` | 只读快照：`playing / loading / currentTime / duration / buffered / volume / muted / error` |
| `on(event, handler)` | `loaded / timeupdate / play / pause / ended / statechange / error`，返回取消订阅函数 |
| `capabilities` | `seek / duration / buffered / volume / audioData`，UI 按它降级（例如 iframe 实现 `buffered=false` 就不画缓冲条、live 流 `duration=false` 显示 LIVE） |

组件侧接线：`watch([file, src])` → `controller.load(...)`；controller 事件 → 同步到视图 refs 并转发 `emit`；卸载时退订 + `controller.dispose()`。

> 新增一种播放形态（以 HLS 为例）：实现同一个 `PlaybackController` 接口 → 用一个工厂按源的 `type` 选择实现即可，`AudioPlayer.vue` 的模板与样式完全不用改。

### 流式播放（HTTP 直链）

演示页 `/player` 里有一个「流式地址」输入框，填 `http(s)://…` 即可直接播（`file` 优先，所以切流式时会清空本地文件）。

已实现的部分：

| 能力 | 做法 |
| --- | --- |
| 二级进度条 | 已缓冲区间画在填充线下方一层（`--mp-buffered`，默认主题红 32% + 轨道色混合）；时长未知或实现不支持 `buffered` 时自动不画 |
| 缓冲高亮 | `loading` 为真时进度轨道上扫过一条高光（`waiting` / `loadstart` 置真，`canplay` / `playing` 置假；seek 不再误触发） |
| 元数据/错误 | `readyState / networkState / error.code` 直接显示在演示页，便于排流式问题 |
| CSP | `media-src` 与 `connect-src` 放开 `http://127.0.0.1:*`（本地流调试用） |

**实测结论（重要）**：

- **dev（页面 origin 是 `http://127.0.0.1:5173`）**：本地 http 直链播放完全正常 —— 时长解析 `159.9s`、`已缓冲区间 1 段`、seek 到 80% 精确命中 `128s`。
- **prod（页面 origin 是 `app://web`，属于 secure origin）**：加载 **`http://`** 媒体会被 Chromium 当**混合内容**处理 —— 请求会先被「自动升级成 https」再失败（CDP 里看到 `net::ERR_ABORTED`，服务端收不到请求），加 `--allow-running-insecure-content` 也救不回来。

所以 prod 下要播本地/自建流，有两条正路：

1. **媒体走 https**（正式 CDN 天然满足，当前实现直接可用）；
2. **宿主 `stream://` 代理**（已实现，见下节）。

### `stream://` 宿主代理（已实现）

宿主注册了一个 secure 的自定义协议 `stream://media/<id>`：页面只拿这个地址当 `src`，真正的 http(s) 请求由**宿主进程**发出，再把响应（含 206/Range）原样转给 CEF。这样既绕开混合内容限制，又能统一注入 `Referer` / `Authorization` / `Cookie` 等头。

```
页面                         宿主
─────────────────────────────────────────────────────
hostApi.registerStream(url, headersJson)
        │                        └─ 登记到内存表，返回 "stream://media/1"
        ▼
<audio src="stream://media/1">
        │                        ┌─ HttpClient 请求上游（转发 Range、注入自定义头）
        └────────────────────────┤   StreamProxyHandler 回写 206/200 + Content-Range
                                 └─ 分块 ReadResponse，内存占用与文件大小无关
```

宿主侧文件：[host/StreamProxy.cs](host/StreamProxy.cs)（注册表 + `IResourceHandler` 实现 + `ISchemeHandlerFactory`），协议在 [host/Program.cs](host/Program.cs) 里注册，JS 入口是 [host/HostApi.cs](host/HostApi.cs) 的 `RegisterStream`。

前端调用：

```ts
import { host } from '@/bridge/host'

const proxied = await host.registerStream('http://127.0.0.1:8787/audio1.mp3', {})
// -> "stream://media/1"，直接当 src 用
```

演示页 `/player` 里对应「走 stream:// 代理播放」按钮，原始 URL 会作为标题来源。

**实测（prod，页面 origin `app://web`）**：

| 检查项 | 结果 |
| --- | --- |
| 元素加载 `stream://media/1` | `readyState=4 networkState=1 err=0`（同一个 http 地址直连时是 `err=4`）✅ |
| 时长 / 缓冲 | `159.9s` / `已缓冲区间 1 段` ✅ |
| seek 到 80% | `128s`，期望 `128s` ✅ |
| 上游 Range 转发 | 日志显示 `bytes=0-`、`bytes=65536-` 等分段请求，逐段命中 ✅ |

> CefSharp 资源处理器的写法踩坑：`Open` 里返回 `true` + `handleRequest=false` 再异步 `callback.Continue()`，在本版本上**不会**继续走 `GetResponseHeaders`；改成**同步阻塞**取响应头（`handleRequest=true`）+ `Read`/`ReadResponse` 双实现后才稳定工作。

`stream://` 必须在所有启动模式下、`Cef.Initialize` 之前注册。原先将其放在 `target.UsesAppScheme` 分支内，导致 `pnpm dev` / `--url` 虽然能成功解析音频，媒体加载仍因未知协议报错误码 4；现已改为无条件注册，`app://` 的前端资源注册仍按加载方式决定。

真实 CEF 播放回归检查（静音，不操作现有播放器窗口；`dev`、`url` 要求 Vite 已启动）：

```powershell
dotnet run --project tools/playback-check -c Release -- dev D:/MusicPlayerData/bili/BV183DFBkEBV.mp3
dotnet run --project tools/playback-check -c Release -- prod D:/MusicPlayerData/bili/BV183DFBkEBV.mp3
dotnet run --project tools/playback-check -c Release -- url D:/MusicPlayerData/bili/BV183DFBkEBV.mp3
```

测试复用宿主启动配置和 `StreamRegistry`，在新的独立 CEF 进程中按播放器的 `preload=metadata` 加载 MP3。检查普通播放 12 秒、前后 seek、暂停恢复，再以 8 倍速从头解码整首歌直到 `ended`；同时定时触发 .NET GC 检查回调资源生命周期。另按字节比较 Range 响应，防止重复跳过或丢失数据。

2026-10-28 曾在预加载元数据后复现约 2.6 秒卡住：CEF 从 `bytes=65536-` 续传时调用 `Skip`，旧实现直接返回失败。现已实现同步 `Skip`，并确认 `Open` 已经由本地 Seek 或上游 206 跳过的偏移，避免再跳一次；同步使用完毕的 CEF 回调及时释放。仅默认 `preload=auto` 的短时播放不足以覆盖这个问题。

### B站 音频（已实现）

粘一个 B站 视频地址（或 BV 号）给宿主，自动解析出音频、转码成 mp3、缓存复用：

```
B站 视频地址 / BV 号
   │
   ├─ view 接口            → title / owner / cid / duration
   ├─ nav 接口             → wbi_img 的 img_key + sub_key（未登录也能拿到）
   ├─ wbi 签名 + playurl   → dash.audio[]（30216 HE-AAC / 30232 / 30280 AAC-LC 192K）
   ├─ 带 Referer+UA 下载   → <数据目录>\bili\<bvid>.m4s
   └─ ffmpeg 转 mp3        → <数据目录>\bili\<bvid>.mp3（+ .json 元数据，命中即复用）
                                │
                                └─ 注册为 stream://media/<id> 给页面播放
```

**为什么必须转码**：B站 的 DASH 音频全是 AAC（`mimeType: audio/mp4`，`codec` `mp4a.40.2`），当前 CEF 官方构建不含 AAC 解码器（见上面的编解码实测），直接播会 `no supported sources`。ffmpeg 转成 mp3 后走 `stream://` 本地文件代理，seek/缓冲都正常。

| 项 | 说明 |
| --- | --- |
| 宿主文件 | [host/Bilibili.cs](host/Bilibili.cs)（wbi 签名 / playurl / 下载 / 转码 / 缓存）、[host/StreamProxy.cs](host/StreamProxy.cs) 的 `RegisterLocalFile` |
| JS 入口 | `host.resolveBilibili(url)` → `{ ok, title, artist, duration, streamUrl, cached, error }` |
| ffmpeg 查找顺序 | `MUSICPLAYER_FFMPEG` 环境变量 → `PATH` → WinGet 安装目录（`Gyan.FFmpeg*`）；找不到会返回明确提示 |
| 缓存 | `<数据目录>\bili\<bvid>.mp3` + `<bvid>.json`，同一 BV 第二次直接命中（status 里会标「命中缓存」） |
| 网络 | B站搜索、签名、视频信息和音频 CDN 均直连（`UseProxy=false`），避免继承 YouTube 使用的系统代理后触发 `code=-412 request was banned`。CEF 与 YouTube 的代理配置仍独立生效 |
| 画质/码率 | 未登录拿 **30280 = AAC-LC 192 Kbps**；更高（Hi-Res / 杜比）需要 `SESSDATA` Cookie，当前未实现 |

**实测（prod，`app://web`）**：

| 检查项 | 结果 |
| --- | --- |
| 解析 `BV1GJ411x7h7` | `【官方 MV】Never Gonna Give You Up - Rick Astley` / `索尼音乐中国` ✅ |
| 时长 | `212.3s`（视频 213s）✅ |
| 播放 | 播放按钮显示「暂停」（正在播）✅ |
| seek 到 50% | `106s`，期望 `106s` ✅ |
| 二次解析 | 命中缓存，mp3 `4978KB` 直接复用 ✅ |

> 合规提醒：只适合个人本地使用。解析播放地址属于绕过平台分发链路，公开分发/商用请走 B站 官方开放接口与授权。

2026-10-28 修复验证：`BV183DFBkEBV` 在原实现下复现 `取视频信息失败：code=-412 request was banned`；改用直连后成功获取视频信息、下载音频并转码为 MP3（视频时长 167 秒）。遇到 HTTP 412 或接口 code=-412 会显示网络排查提示；`UseProxy=false` 无法绕过 VPN/TUN 在系统网络层接管的流量，此时需在对应客户端为 B站设置直连。修改宿主代码后需退出并重新运行 `pnpm dev` 或 `pnpm start`，只刷新页面不会加载新的 C# 代码。

### YouTube 音频（已实现）

和 B站 复用同一条「下载 → 转码 → 缓存 → `stream://` 本地播放」链路，只是取流换成 `yt-dlp`：

```
YouTube 地址
   └─ yt-dlp -f "bestaudio/best[acodec!=none]" -x --audio-format mp3（首次约 10~40s）
        └─ <数据目录>\youtube\<videoId>.mp3 + index.json（url 命中即跳过 yt-dlp）
             └─ stream://media/<id> 给页面播放
```

| 项 | 说明 |
| --- | --- |
| 宿主文件 | [host/Youtube.cs](host/Youtube.cs) |
| JS 入口 | `host.resolveYouTube(url)`，返回结构同 `resolveBilibili` |
| yt-dlp 查找 | `MUSICPLAYER_YTDLP` → PATH 上的 `yt-dlp.exe` → `%APPDATA%\Python\Python313\Scripts\yt-dlp.exe` → `python -m yt_dlp` |
| 安装 yt-dlp | `python -m pip install --user -U "yt-dlp[default]"`，需要包含与 yt-dlp 匹配的 `yt-dlp-ejs` 签名解析脚本；镜像可加 `-i https://mirrors.aliyun.com/pypi/simple/` |
| JavaScript 运行时 | YouTube 签名解析需要支持的运行时；项目自动启用 PATH 中的 `node.exe`（Node 22+），也可用 `MUSICPLAYER_NODE` 指定完整路径。没有 Node 时仍允许 yt-dlp 使用默认的 Deno |
| 代理 | 不传 `--proxy` 时自动读 WinINET 注册表（本机是 `127.0.0.1:7897`）；也可 `--proxy http://127.0.0.1:7897` 或 `MUSICPLAYER_PROXY` |
| 缓存 | `<数据目录>\youtube\<id>.mp3` + `index.json`，`youtu.be/xxx` 与 `watch?v=xxx` 归一成同一个 key |

**实测（prod，`app://web`）**：

| 检查项 | 结果 |
| --- | --- |
| 解析 `watch?v=dQw4w9WgXcQ` | `Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)` / `Rick Astley` ✅ |
| 时长 | `213.0s` ✅ |
| 播放 / seek 50% | 正在播 / `107s`（期望 107）✅ |
| 二次解析（`youtu.be` 短链） | 命中缓存，`3657KB` 直接复用 ✅ |

**为什么不用 iframe 播放器**：实测 `app://web` 和 http 两种 origin 下，`iframe_api` 与 embed 页都能拿到 200，但 iframe 文档始终不触发 `load`、画面空白 —— 播放器资源在代理不稳时会挂住；而且 iframe 方案拿不到缓冲区间、无法和自绘进度条精确同步。yt-dlp 取文件这条路一次性下载即完，播放行为与本地文件完全一致。

#### YouTube 报 Requested format is not available

若 `--list-formats` 只列出 `sb0`～`sb3` 的 storyboard 图片，并出现 `Signature solving failed` 或 `n challenge solving failed`，说明签名解析未完成，不能据此认为原视频没有音轨。按 [yt-dlp EJS 官方说明](https://github.com/yt-dlp/yt-dlp/wiki/EJS)安装匹配的 `yt-dlp-ejs` 并启用支持的 JS 运行时。Node 已安装也需要传入 `--js-runtimes node`，宿主现在自动处理此参数；使用 Python 安装的 yt-dlp 可运行上表中的 `yt-dlp[default]` 安装命令。解析器保留警告用于诊断，不再用 `--no-warnings` 隐藏签名失败。

音频格式选择优先 `bestaudio`，没有独立音轨时回退到含音频的视频格式，继续由 ffmpeg 提取 MP3。2026-10-28 用 `-6NfPHeWxI4` 实测：原配置仅返回 storyboard，启用 Node 并安装配套 EJS 后成功下载、转码为约 166 秒 MP3。可用以下命令复现下载检查（Cookie 路径按实际配置）：

```powershell
$env:MUSICPLAYER_YOUTUBE_COOKIES = 'D:\MusicPlayerData\youtube-cookies.txt'
dotnet run --project tools/media-search-check -- --resolve-youtube 'https://www.youtube.com/watch?v=-6NfPHeWxI4'
```

#### YouTube 要求登录或确认不是机器人

当 yt-dlp 返回 `Sign in to confirm you’re not a bot`，先在浏览器里使用与播放器相同的代理出口打开该视频，手动完成登录/人机验证并确认能播放。项目支持两种显式配置，搜索和解析共用；没有配置时不会自动提取浏览器登录信息。

方式一：直接使用已登录的浏览器（`edge` / `chrome` / `firefox`，也支持 yt-dlp 的 profile 语法）。完成验证后退出浏览器和播放器，在同一个 PowerShell 窗口设置并启动，例如：

```powershell
$env:MUSICPLAYER_YOUTUBE_COOKIES_BROWSER = 'edge'
pnpm dev
```

方式二：浏览器 Cookie 数据库被锁定或无法解密时，按 [yt-dlp 官方说明](https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies)导出 YouTube 的 Netscape 格式 Cookie 文件到项目外，再指定路径：

```powershell
$env:MUSICPLAYER_YOUTUBE_COOKIES = 'D:\MusicPlayerData\youtube-cookies.txt'
pnpm dev
```

Cookie 文件优先于浏览器配置；文件不存在会直接提示。这里的环境变量只影响从该终端启动的进程。Cookie 是登录凭据，不要上传或提交到仓库；过期后需重新导出。官方说明还提醒使用账号下载存在账号被限制的风险，按需使用，避免频繁请求。若配置后仍触发验证，应检查 Cookie 是否过期、profile 是否正确，以及浏览器与播放器的代理出口是否一致；Cookie 无法保证消除所有服务端限制。

**代理与「被 Google 拦」的排查顺序**（本机踩过的完整路径）：

| 现象 | 含义 | 处理 |
| --- | --- | --- |
| `google.com/sorry/index` 重定向 | 出口 IP 被 Google 判可疑（验证码） | 换节点后重试；或在**同一 IP 的浏览器里**解一次验证码，让该 IP 解除标记 |
| `iframe_api` 200 但 iframe 空白 | 代理能连通但大资源或子资源超时 | 换更稳的节点；或直接用 yt-dlp 路线（本仓库已实现） |
| `curl` 报 `SEC_E_UNTRUSTED_ROOT` / `CRYPT_E_REVOCATION_OFFLINE` | curl 走直连，没走系统代理 | 加 `--proxy http://127.0.0.1:7897`；证书吊销检查可 `--ssl-no-revoke` |
| yt-dlp 报超时 / 403 | Python 不读 WinINET 代理 | 传 `--proxy`（宿主已自动从注册表读取并传给 yt-dlp） |

## 按歌曲信息搜索视频

启动 `pnpm dev` 或构建后 `pnpm start`，在首页点「打开搜索页」（`/#/search`）。歌曲名必填，作者和专辑可选；两个平台各自显示结果，默认各 6 条，支持打开、复制视频链接。搜索只获取视频信息，点击「解析播放」才会下载音频。

可复用组件在 `renderer/src/components/VideoSearch.vue`，父页面通过 `result` 事件接收每个平台的结果：

```vue
<script setup lang="ts">
import { ref } from 'vue'
import VideoSearch from '@/components/VideoSearch.vue'
import type { MediaSearchResult } from '@/bridge/host'

const song = ref('晴天')
const artist = ref('周杰伦')
const album = ref('叶惠美')
const links = ref<Record<string, string[]>>({})

function receive(result: MediaSearchResult) {
  links.value[result.source] = result.items.map(item => item.url)
}
</script>

<template>
  <VideoSearch
    v-model:song="song"
    v-model:artist="artist"
    v-model:album="album"
    :limit="6"
    @result="receive"
  />
</template>
```

`result` 的结构为 `{ ok, source, keyword, items, error }`。`items` 包含 `id`、`title`、`author`（UP 主或频道名）、`url`（视频页链接）、`thumbnail`、`duration`（秒）、`playCount`；每个平台结束时分别触发一次。组件也通过模板 ref 暴露 `search()` 和 `clear()`；清空或卸载组件会忽略旧请求的结果。

仅调用搜索逻辑可使用 `renderer/src/search/songVideoSearch.ts` 的 `searchSongVideos`，传入歌曲信息和两个搜索函数即可，无需渲染组件。

- B站搜索由 C# 宿主请求 Web 搜索接口，复用 WBI 签名。接口可能触发风控，失败时提供网页版搜索入口。
- YouTube 通过 [yt-dlp](https://github.com/yt-dlp/yt-dlp#usage-and-options) 的 `ytsearchN:` 与 `--flat-playlist` 获取结果，需要安装 yt-dlp；搜索无需 ffmpeg 或 YouTube API Key。宿主会沿用系统代理或启动参数 `--proxy`。
- `pnpm dev:web` 只有浏览器页面，没有 C# 宿主，不能直接获取两站结果，会显示搜索页入口。

验证命令：

```powershell
pnpm --filter renderer test # 测试使用 Node 22.18+ 的 TypeScript 类型擦除
dotnet run --project tools/media-search-check # 本地响应解析检查，无网络请求
dotnet run --project tools/media-search-check -- --live # 实际请求两站，各取 3 条链接
dotnet run --project tools/media-search-check -- --resolve-bili https://www.bilibili.com/video/BV183DFBkEBV # 下载转码验证，缓存到系统临时目录 MusicPlayerBiliProbe
```

2026-10-28 用「晴天 周杰伦 叶惠美」实测，两站均返回 3 条视频链接，YouTube 包含官方 MV。搜索结果受关键词和平台排序影响，不保证是原曲或官方版本。

## 数据目录（CEF profile / 缓存 / localStorage）

默认在 **`D:\MusicPlayerData`**，也可以改到任意盘。优先级从高到低：

| 优先级 | 方式 | 例子 |
| --- | --- | --- |
| 1 | 命令行 `--user-data <dir>` | `MusicPlayer.Host.exe --user-data E:\MusicPlayerData` |
| 2 | 环境变量 `MUSICPLAYER_USER_DATA` | `$env:MUSICPLAYER_USER_DATA='E:\MusicPlayerData'` |
| 3 | 便携模式：exe 同目录放一个 `portable.txt`（空文件也行） | 数据落在 `exe目录\data` |
| 4 | 默认 | `D:\MusicPlayerData` |

- **默认值的兜底**：如果 D 盘不存在或目录不可写（换台机器、盘符被占用），会自动回退到 `%LOCALAPPDATA%\MusicPlayer\ver2` 并继续启动，回退结果会写在来源标签里（如「D:\MusicPlayerData 不可用，已回退 LOCALAPPDATA」）。而**显式指定**的路径（1、2 两种方式）不可用时会直接报错，不会偷偷换位置。
- **相对路径按 exe 所在目录解析**（不是当前工作目录），所以 `--user-data data` 就是 `exe目录\data`。
- 页面「运行环境」卡片会显示当前实际用的目录和它来自哪种配置（命令行 / 环境变量 / 便携模式 / 默认位置），排查时一眼就能看到：

  ![数据目录来源](docs/datadir.png)

- 想搬走已有数据：先关掉应用，把旧目录整个拷到新位置，再用上面的方式指过去即可（缓存可以不清，CEF 会自己校验）。从旧版升级过来时，把 `%LOCALAPPDATA%\MusicPlayer\ver2` 里的内容拷进 `D:\MusicPlayerData` 即可延续登录态。
- 注意事项：路径含中文没问题（已实测 `D:\code\项目\音乐播放器\...` 可用）；不要指向网络盘 / OneDrive 同步目录 / 只读盘，CEF 对缓存目录的写入要求较高，容易启动失败。目录不可写时会弹出带完整路径的中文错误提示，而不是 CEF 内部的报错。

## 为播放器预留的配置

- `autoplay-policy=no-user-gesture-required`：无需用户点击即可自动播放。
- 用户数据 / 缓存目录可配置（见上一节），登录态、播放列表可持久化。
- CSP 的 `media-src` 已放行 `file:` / `blob:` / `stream:` / dev server 与本地 http（`http://127.0.0.1:*`）；http 媒体走 `stream://` 宿主代理（已实现，见上文「`stream://` 宿主代理」）。
- 窗口尺寸会按屏幕可用区域自适应，小屏也不会撑出屏幕。

## 踩过的坑（都已处理）

- **TypeScript 固定在 5.9**：TS 7 移除了 `./lib/tsc` 子路径导出，`vue-tsc` 3 还依赖它，用 TS 7 会直接报 `ERR_PACKAGE_PATH_NOT_EXPORTED`。编辑器请用工作区版本（`.vscode/settings.json` 已指定 `typescript.tsdk`，首次打开时选 “Use Workspace Version”）。
- **tsconfig 里不要用 `baseUrl`**：TS 6/7 已将它标记为弃用并会移除。`paths` 从 TS 4.1 起默认相对 tsconfig 所在目录解析，所以 `"@/*": ["./src/*"]` 直接可用。
- **CefSharp 154 的 `CefSettings` 在 `CefSharp.WinForms` 命名空间**，不在 `CefSharp`；`CefSettingsBase` 是抽象类。
- `Cef.Initialize` / `Cef.IsInitialized` 返回 `bool?`，不能直接 `if (...)`。
- 必须 `PlatformTarget=x64` + `RuntimeIdentifier=win-x64`，否则 CEF 原生库不会拷到输出目录。
- 页面必须在 `app.mount()` 之前完成 `BindObjectAsync`，否则首屏拿到的是降级数据。
- **远程调试端口要避开 Windows 保留段**：本机 `netsh int ipv4 show excludedportrange protocol=tcp` 里有 `9960–10059` 等保留区间，`--debug-port` 选到里面会让 `TcpListener` 绑定失败，宿主按设计把端口降级成 `0`（调试直接关闭，且没有任何报错）。选 `92xx` 之类没被保留的端口即可。
- **自定义 scheme 里加载 `http://` 媒体会被混合内容拦掉**：`app://` 是 secure origin，Chromium 会把 http 媒体「自动升级成 https」再失败（`net::ERR_ABORTED`，上游服务端根本收不到请求）。解决方案是 `stream://` 宿主代理（见上文），不要指望 `--allow-running-insecure-content`。
- **CefSharp 资源处理器优先用同步 `Open`**：异步 `callback.Continue()` 那条路在本版本不会继续调用 `GetResponseHeaders`，结果就是元素一直 `readyState=0` 直到超时。
