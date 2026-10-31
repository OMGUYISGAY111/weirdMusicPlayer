# 播放内核层

## 概述
把对 &lt;audio&gt; 的操作、断流恢复、宿主诊断接口封装成 PlaybackController 契约，让上层组件只通过这一个接口和播放实现通信

## 文件负责功能

### 契约与能力上报
types.ts - 契约
supportedAudioTypes.ts   把运行时解码能力报给宿主

### 内部
mediaElementController.ts -  &lt;audio&gt; 实现 + 状态机 + 诊断
streamRecovery.ts        断流重试/续播（无 DOM、无宿主依赖）

### 组合根
hostMediaController.ts   把 host 的部分功能注入控制器

## 理解实现

<details>

<summary>点开看实现细节（链路 / 状态机 / 断流恢复 / 扩展）</summary>

### 一条链路的全貌

```
AudioPlayer.vue / QueuePlayer.vue          只认 PlaybackController
        │  load / play / pause / seek / setVolume
        ▼
createHostMediaController()                 组合根（11 行，唯一碰 host 的地方）
        │  注入 sourceError / recoverSource / log
        ▼
createMediaElementController()              内核：new Audio() + 状态机
        │                            │
        │ <audio> 事件                │ 需要重试时
        ▼                            ▼
   snapshot / emit         createStreamRecovery()（纯逻辑，可单测）
                                     │ renew / restore
                                     ▼
                            bridge/host.ts → HostApi.cs
                            （重新注册 stream://、取流、FFmpeg、PlaybackLog）
```

三句话概括这一层：

- **UI 不碰 `<audio>`**：元素在控制器内部 `new Audio()`，不进 DOM，组件拿不到也不需要 DOM ref。
- **内核不碰宿主**：宿主能力只在 `hostMediaController.ts` 注入；不注入时它就是纯 `<audio>` 播放器，可在 Node 里直接单测。
- **重试不碰 DOM**：`streamRecovery.ts` 只认 `suspend / restore / resumed / failed` 四个回调，所以能脱离浏览器验证。

### 注入口（options）

`createMediaElementController(initialVolume, options)` 的五个注入口就是宿主与测试的分界线：

| 选项 | 用途 |
| --- | --- |
| `sourceError(url)` | 问宿主“这条流为什么失败”，返回 `message` + `recoverable` |
| `recoverSource(url)` | 让宿主重新解析并注册新流，返回新的 `stream://` 地址 |
| `log(event, details)` | 写诊断事件（宿主侧落 `playback.jsonl`） |
| `recoveryDelayMs` | 恢复退避基数，默认 1500ms |
| `errorCheckIntervalMs` | 宿主错误轮询间隔，默认 1000ms |

后两个只在测试里显式传。不注入 `sourceError` / `recoverSource` 时，错误直接落成 `error` 状态、不尝试恢复。

### 状态机：snapshot + 无参事件

内部只有一份 `PlaybackSnapshot`，所有变更都走 `patch(next, event?)`：先合并状态并 `emit('statechange')`，有具名事件时再 `emit(event)`。

因此**事件回调不带参数**，监听方必须自己读快照：

```ts
const off = controller.on('timeupdate', () => {
  const s = controller.snapshot() // 不要指望回调参数
  render(s.currentTime, s.duration)
})
```

监听器异常会被 `try/catch` 兜住并 `console.error`，一个坏监听器不会中断播放；`on()` 返回取消订阅函数。

### 四个命令的关键实现

| 命令 | 关键点 |
| --- | --- |
| `load()` | 释放上一个 `createObjectURL` → `pause()` → 重置快照 → `file` 优先、其次 `url` → 启动诊断与错误轮询；空源时 `removeAttribute('src')` 并置 `loading: false` |
| `play()` | 没有 `src` 直接返回；置 `wantsPlay`（自动恢复的判据）；先取 `loadGeneration` 再 `await el.play()`，失败时**代已变就吞掉**——切歌会让旧 Promise 以 AbortError 失败，不能记成新曲目的错误 |
| `pause()` | `wantsPlay = false` + `recovery.cancel()` + `++loadGeneration`，让所有在途异步结果作废 |
| `seek()` | 有恢复任务时交给 `recovery.seek()`（只改“要回到的位置”），否则直接改 `el.currentTime`；时长未知时不做上界钳制 |

两个容易忽略的细节：

- `setVolume(v)` 会顺带 `el.muted = false`——拖音量条等于取消静音；静音状态只由 `setMuted()` 管。
- 内部还有 `needsRecovery` 标志：恢复挂起期间为真，此时 `timeupdate` 被忽略，进度停在断点不会乱跳；用户这时点播放会直接跳到 `recovery.start()`。

### 错误从哪来：两条通道

1. **媒体自身**：`<audio>` 的 `error` 事件，解码/加载失败的常规路径；错误码 3/4 且源是 `stream://` 时，再向宿主要一次原因。
2. **宿主轮询**：`stream://` 源每秒调一次 `host.getStreamFailure(url)`，专门抓“CEF 不报 media error、但后台取流或 FFmpeg 已经失败”的情况；用 `pending` 防请求叠加、用代校验丢弃过期结果。

两条通道最后都汇到 `handleFailure(message, recoverable)`：可恢复 + 有播放意图 + 是 `stream://` + 注入了 `recoverSource` → 进入恢复；否则 `fail()` 落成 `error` 状态并 `emit('error')`。

### 断流恢复的完整时序

```
<audio> 报错 / 轮询发现失败
  └─ handleFailure(recoverable: true)
       └─ recovery.start(url)
            ├─ suspend()    暂停、记住 position、++generation、清错误、置 loading
            ├─ 循环：等待（1.5s 起、翻倍、封顶 15s）→ host.recoverStream(url)
            │    ├─ 拿到新地址 → restore()
            │    │    ├─ el.src = 新地址
            │    │    ├─ 等 loadedmetadata
            │    │    ├─ currentTime = min(断点, 时长) → 等 seeked
            │    │    └─ el.play()（25s 超时）
            │    ├─ 没拿到地址且 recoverable === false → failed() 终止
            │    └─ 没拿到地址但可恢复（风控 412 / 网络） → 继续等
            └─ resumed()    置 playing、重启错误轮询
```

几条约定：

- `cancel()` 之后任何旧结果都不许继续播——`AbortSignal` 会同时中止等待、元数据、seek 和 `play()`。
- 恢复期间调 `seek()` 只更新“要回到的位置”，真正的跳转发生在 `restore()` 里。
- `recoverable === false`（文件被删、权限、解码配置）必须立刻报错，不能无限重试。
- `restore()` 失败时会先问 `sourceError` 要原因；媒体错误码 3 兜底成「音频无法解码」。

### 生命周期与所有权

`dispose()`：停两个定时器 → 取消恢复 → `pause()` → 清 `src` → revoke objectURL → 清监听 → `el.remove()`。`load()` 里也会 revoke 上一个 objectURL，换源不会泄漏。

谁创建谁释放：`AudioPlayer.vue` 只在**自己创建的**控制器上 `dispose()`；通过 `controller` prop 注入时，生命周期归注入方（`QueuePlayer.vue` 卸载时自己 dispose）。

### capabilities 决定 UI 降级

```ts
const CAPABILITIES = { seek: true, duration: true, buffered: true, volume: true, audioData: false }
```

UI 只按能力渲染，不按实现类型判断：`buffered: false` 就不画缓冲条，`duration: false` 就该显示 LIVE，`audioData` 是给未来的频谱/可视化留的位（当前无实现）。将来接 HLS / iframe，只改这几行声明，模板里不需要加 `if (isIframe)`。

### 新增一种播放实现

1. 实现 `PlaybackController`（见 `types.ts`），自己管媒体；没有媒体元素就让 `element` 返回 `null`。
2. 在 `hostMediaController.ts` 那层按来源类型选工厂，或新开一个 `xxxController.ts` 并接同样的宿主注入口。
3. 检查两个已经依赖 `element` 的地方：
   - `playlist/playbackAdapter.ts`：`position()` 已用 `controller.element?.currentTime ?? snapshot().currentTime` 兜底；用 `element.ended` 判断自然结束的地方，在 `element === null` 时会退化成普通暂停语义。
   - `views/PlayerView.vue`：底层诊断会显示 `no element`，属预期。

### 验证

```powershell
pnpm --filter renderer test                     # 内核、解码能力探测、adapter 的 node 测试
node renderer/tests/buildPlaybackHarness.mjs    # 把内核打成 IIFE harness
dotnet run --project tools/playback-check -c Release -- recovery <fixture.wav>   # 真实 CEF 里跑恢复
```

单测用假 `Audio`（`FakeAudio` / `RecoveringAudio`）驱动，不需要浏览器；真实 CEF 下的恢复与解码落盘由 `tools/playback-check` 覆盖。
</details>