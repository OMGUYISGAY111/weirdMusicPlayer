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
- CSP 的 `media-src` 已放行 `file:` / `blob:` / dev server，本地音频可直接用 `<audio>`；更规范的做法是再加一个 `music://` 自定义协议（照 `Program.cs` 里 `app://` 的写法）。
- 窗口尺寸会按屏幕可用区域自适应，小屏也不会撑出屏幕。

## 踩过的坑（都已处理）

- **TypeScript 固定在 5.9**：TS 7 移除了 `./lib/tsc` 子路径导出，`vue-tsc` 3 还依赖它，用 TS 7 会直接报 `ERR_PACKAGE_PATH_NOT_EXPORTED`。编辑器请用工作区版本（`.vscode/settings.json` 已指定 `typescript.tsdk`，首次打开时选 “Use Workspace Version”）。
- **tsconfig 里不要用 `baseUrl`**：TS 6/7 已将它标记为弃用并会移除。`paths` 从 TS 4.1 起默认相对 tsconfig 所在目录解析，所以 `"@/*": ["./src/*"]` 直接可用。
- **CefSharp 154 的 `CefSettings` 在 `CefSharp.WinForms` 命名空间**，不在 `CefSharp`；`CefSettingsBase` 是抽象类。
- `Cef.Initialize` / `Cef.IsInitialized` 返回 `bool?`，不能直接 `if (...)`。
- 必须 `PlatformTarget=x64` + `RuntimeIdentifier=win-x64`，否则 CEF 原生库不会拷到输出目录。
- 页面必须在 `app.mount()` 之前完成 `BindObjectAsync`，否则首屏拿到的是降级数据。
