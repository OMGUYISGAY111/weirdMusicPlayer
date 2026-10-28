# 播放列表

`playlist.ts` 是无框架的队列逻辑，只依赖 `PlaybackSource` 类型。通过 `createPlaylist(resolveTrack)` 注入音频解析函数，通过 `connect({ load, play, pause })` 接入播放器。`snapshot/subscribe` 对外提供状态。

`hostAdapter.ts` 把搜索结果转成统一曲目，并在选中播放时调用宿主解析视频。列表保存视频原始链接，不缓存临时流地址。音频直链和本地 File 可用 `source.kind = 'audio'` 加入。

`stores/playlist.ts` 用 Pinia 发布共享状态。`Playlist.vue` 只接收 props 并发出 play/remove/clear 事件，不依赖搜索、宿主或 store。`QueuePlayer.vue` 在 App 内持有播放器，离开搜索页也继续播放。AudioPlayer 可选择注入控制器，原来的独立用法保持兼容。

`playbackAdapter.ts` 连接播放器和列表：订阅 `PlaybackController` 的 `ended` 事件并调用列表的 `ended()`，列表根据自己的 `state.mode` 决定后续动作，再通过注入的 `load/play` 命令播放。播放器和 AudioPlayer 均不读取列表及模式，自动切歌也不依赖界面组件转发事件。断开连接时会取消所有事件订阅。

底部「播放模式」可切换以下模式，也可调用 `playlist.setMode(mode)`：

| mode | 模式 | 自然播放结束 |
| --- | --- | --- |
| `sequential` | 顺序播放（默认） | 下一首，队尾停止 |
| `repeat-all` | 列表循环 | 下一首，队尾回到第一首 |
| `repeat-one` | 单曲循环 | 重播当前歌曲 |
| `shuffle` | 随机播放 | 随机选择另一首，仅一首时重播 |

模式变更从下一次操作生效，不打断当前播放。单曲循环时，手动上一首/下一首仍可切歌；随机模式的上一首返回最近播放记录（最多 100 条）。清空列表保留模式。解析或播放错误不会被当成自然结束，也不会自动无限重试。

其他页面加入音频的例子：

```ts
const playlist = usePlaylistStore()
const id = playlist.add({
  id: 'my-library:123',
  title: '歌曲名',
  artist: '作者',
  source: { kind: 'audio', url: 'https://example.com/song.mp3' },
})
await playlist.play(id) // 只添加时不调用 play
```

ID 在列表内唯一，重复添加返回原 ID。解析或播放失败时保留当前项，支持重试或手动切歌。移除播放中的曲目会播放相邻曲目，清空或取消解析会使未完成请求的结果失效（宿主下载任务可能仍会在后台完成）。

`persistence.ts` 通过订阅列表实现自动持久化，不向核心引入浏览器依赖。存储 key 为 `music-player.playlist.v1`，保存歌曲顺序、标题/作者/封面、原始来源、模式、当前曲目和最近播放记录。启动时验证存档、去重并过滤无效条目，再通过 `createPlaylist` 的 `initial` 选项恢复；不自动解析、加载或播放，点击底部「播放」或列表歌曲才开始。

应用使用当前站点的 localStorage，CEF 已配置磁盘缓存目录，数据随所选用户数据目录保留。开发服务器和正式版 origin 不同，分别保存；清除站点数据或更换用户数据目录会影响恢复。添加、移除、清空和模式/当前曲目变化立即写入，单纯播放状态变化不会重复写盘。存储被禁用、空间不足或存档损坏时不影响当前播放，搜索页显示存储错误。

视频保存原始页面链接，恢复后播放时重新解析。不会保存播放器的临时解析地址、错误、播放进度或自动播放状态。通过文件选择器获得的 `File` 以及 `blob:`、`stream:` 临时条目仍可在本次会话播放，但不会持久化；需要再次选择文件。直接音频来源支持持久化有效的 http(s)、app 和 file URL（地址指向的资源仍需存在）。

运行 `pnpm --filter renderer test` 验证队列和异步切换行为，`pnpm build:web` 验证类型及构建。
