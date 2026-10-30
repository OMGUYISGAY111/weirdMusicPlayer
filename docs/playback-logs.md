# 播放诊断日志

默认自动记录在当前应用数据目录的 `logs` 子目录。默认数据目录为 `D:\MusicPlayerData`；使用 `--user-data`、环境变量或便携模式时随对应目录变化。首页「打开诊断日志」或播放栏「打开日志」会先刷新待写记录，再打开该文件夹。

复现问题后，把 `playback.jsonl` 和同目录的 `playback.1.jsonl` 等文件一起提供，并说明出现问题的大致时间和视频 BV 号/ID，即可查看卡住前后的完整过程。日志只保存在本机，不自动上传；无需提供音频、Cookie 或浏览器配置。

## 文件与记录

- UTF-8 JSONL：每行一个 JSON 对象，中文直接可读。
- `playback.jsonl` 为最新记录；达到约 2 MiB 后轮转到 `.1`，最多保留 `.1`～`.4`，总量约 10 MiB。
- `time` 使用**本机时区**的 ISO 8601 时间（带偏移量，如 `2026-10-30T22:54:12.3576025+08:00`），和系统时钟一致；`session` 区分每次应用启动，`pid` 区分同时运行的进程。
- `component`、`event` 标识事件，`data.source` 的 `stream://media/<id>` 用于关联前后端，后台同时记录公共视频 ID。

主要事件：

| 类别 | 内容 |
| --- | --- |
| 播放控制 | 换源、加载元数据、开始/暂停、seek、正常结束 |
| 卡顿 | `buffering_start`、网络 `stalled`、有播放意图但进度超过 10 秒不变的 `progress_stalled` |
| 恢复 | 开始等待、每次尝试和退避时间、刷新地址失败原因、成功续播、用户取消 |
| 快照 | 当前时间、总时长、缓冲区间、播放/加载状态、音量/静音、媒体 `readyState/networkState/errorCode`，以及每 30 秒一次的活动快照 |
| 宿主取流 | 流注册、播放方式、HTTP/Range 状态与长度、读取失败、取消请求及已读字节数 |
| 解码 | FFmpeg 启动时间点、进程 ID、错误诊断、已解码字节、停止 |
| 其他错误 | 视频解析失败、前端 JS/Vue 异常、console 警告/错误和宿主异常 |

正常的静音片段不会被当作故障。进度不变的日志仅用于诊断，不会改变缓冲或恢复行为。

## 脱敏与可靠性

写入前由宿主统一过滤 Cookie、Authorization、token 等字段及文本凭据；网络 URL 仅保留协议和主机，不保存签名、查询参数、用户信息或资源路径；本地文件路径隐藏。可关联的流 ID、视频 ID、状态码和播放时间保留。

日志通过有界队列异步写入，支持多线程/多进程轮转；磁盘不可写或日志队列满时不会中断播放。日志为尽力记录，强制结束进程、磁盘故障或极端错误洪峰时可能丢失尚未写入的部分。异常内容较长时会截断，打开目录和正常退出前会刷新队列。

实现位于 `host/PlaybackLog.cs`。媒体控制器通过可注入 `log` 回调记录事件，不依赖宿主存储。验证命令：

```powershell
dotnet run --project tools/log-check
pnpm --filter renderer test
node renderer/tests/buildPlaybackHarness.mjs
dotnet run --project tools/playback-check -c Release -- recovery (Join-Path $env:TEMP 'MusicPlayerStreamChecks/fixture-24s.wav')
```

最后一项使用流式播放文档生成的合成音频夹具，验证真实 CEF 中恢复事件和后台解码日志的落盘。
