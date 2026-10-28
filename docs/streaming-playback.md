# 不生成媒体文件的播放链路

播放卡顿和异常现在自动写入诊断日志，位置、字段及提交方式见 [播放诊断日志](playback-logs.md)。

播放列表仍保存视频页地址。选择歌曲时，宿主只获取音轨 URL 和元数据，音频数据在播放器请求 `stream://` 时才开始接收，不再创建 `.m4s`、`.mp3`、媒体索引或完整音频缓存。旧版本已有的缓存文件不会被自动删除。

## 编码选择

前端 `supportedAudioTypes()` 用当前 CEF 的 `Audio.canPlayType()` 查询精确 MIME/codec，并随解析请求发送给宿主。支持的格式使用 HTTP 直接代理，保留 Range、必要的 Referer/User-Agent 及解析时的代理出口。YouTube 优先选择 WebM/Opus；B站优先 AAC-LC 192K，如果 CEF 无法解码则进入实时转码。

`host.resolveBilibili(url)` / `host.resolveYouTube(url)` 的返回值继续提供 `streamUrl`，新增 `playbackMode: 'direct' | 'transcode'`。兼容字段 `cached` 固定为 false。播放列表及结束事件/播放模式逻辑无需感知流的实现。

## 实时转码与跳转

FFmpeg 直接读取远端音轨，将 PCM 输出到标准输出管道。`VirtualWaveStream` 只保存 44 字节 WAV 头，后续音频按播放器的读取需求生成；管道背压限制提前解码和内存占用。响应带 `Cache-Control: no-store`。本机 PCM 管道约 192 KB/s，不代表向平台请求的网络码率。

采用 48 kHz、双声道、16 bit PCM，使字节偏移与采样时间可以精确换算。CEF 发起 Range 请求或前后拖动时，从目标时间启动 FFmpeg 解码并处理采样内的字节偏移，无需先转码整首歌。WAV 时长来自平台元数据；固定样本数保证 Content-Length 一致，因此来源时长必须准确。读取超时会结束请求；取消、切歌和请求释放会终止对应转码进程。

转码不使用无限 `apad`：未解出音频、解码报错或明显提前结束均使请求失败，不能用静音补满剩余歌曲。只有已输出真实采样且 FFmpeg 干净退出时，允许最多 250ms 的末尾时长误差补齐。媒体报错会暂停并停止进度更新。测试除时间推进外，还检查 PCM 采样，避免把全零音频误当成成功播放；音源自身的静音片段不作为运行时故障判据。

CEF 在缺失数据时可能反复续传且不发出媒体 `error`。宿主记录已失败流并阻止继续解码，`GetStreamFailure` 诊断接口供应用控制器每秒检查，并区分临时故障和永久失败。临时网络故障时暂停底层媒体、保留播放意图和时间，显示“缓冲中”，由 `RecoverStream` 根据原视频链接重新解析地址，在加载元数据后 seek 回原位置并自动续播。后台连接以 1.5 秒开始退避，最长每 15 秒尝试一次，用户无需操作；删除、权限限制、缺少解码器等无法恢复的情况才显示错误。

缓冲期间可以暂停、切歌、清空或关闭；这些操作会取消恢复，旧结果不能重新播放。等待时拖动进度会改变续播位置。播放列表只同步 `buffering` 状态，不修改当前曲目、历史或触发下一首。纯媒体控制器通过可注入的诊断和刷新函数保持与宿主解耦，恢复调度在 `streamRecovery.ts` 中。

B站的 API 和媒体请求使用直连；YouTube 沿用显式配置或 Windows 系统代理。登录与人机验证处理沿用已有的显式 Cookie 配置，不会自动读取浏览器账号。yt-dlp 返回的 Cookie 保留域名、路径和 Secure 作用域，直接代理与 FFmpeg 都按作用域使用，禁止把 Cookie 当作跨域通用请求头转发。临时音轨地址过期或平台拒绝访问时，可以点击重试重新解析。

目前实时转码面向有确定时长的音乐，标准 WAV 单个音轨上限约 6 小时，不支持未知时长/直播的转码。FFmpeg 转码路径需要 HTTP 代理入口；原始流路径使用 .NET HTTP 代理。缓存目录中仍可能出现 CEF 自身的站点数据，这与保存完整音乐文件不同。

## 验证

```powershell
pnpm build:web
pnpm --filter renderer test
dotnet build host/MusicPlayer.Host.csproj -c Release
dotnet run --project tools/media-search-check

# 使用项目外的合成音频夹具，避免依赖平台或真实账号。
$fixture = Join-Path $env:TEMP 'MusicPlayerStreamChecks'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
ffmpeg -nostdin -hide_banner -loglevel error -y -f lavfi -i 'sine=frequency=440:duration=24:sample_rate=48000' -ac 2 -c:a pcm_s16le (Join-Path $fixture 'fixture-24s.wav')
ffmpeg -nostdin -hide_banner -loglevel error -y -i (Join-Path $fixture 'fixture-24s.wav') -c:a libopus (Join-Path $fixture 'fixture-24s.webm')
dotnet run --project tools/playback-check -c Release -- transcode (Join-Path $fixture 'fixture-24s.wav')
dotnet run --project tools/playback-check -c Release -- direct (Join-Path $fixture 'fixture-24s.webm')
dotnet run --project tools/playback-check -c Release -- failure (Join-Path $fixture 'fixture-24s.wav')
node renderer/tests/buildPlaybackHarness.mjs
dotnet run --project tools/playback-check -c Release -- recovery (Join-Path $fixture 'fixture-24s.wav')

# 可选的实际平台检查，会受网络、账号和平台状态影响。
dotnet run --project tools/playback-check -c Release -- bili BV1GJ411x7h7
dotnet run --project tools/playback-check -c Release -- youtube 'https://www.youtube.com/watch?v=-6NfPHeWxI4'
```

CEF 检查覆盖连续播放、前后跳转、暂停恢复和自然结束。转码检查还验证任意字节 Range 与顺序解码一致、只读取 WAV 头不启动解码器、取消后进程退出；直接流夹具验证必要请求头转发与 WebM/Opus 解码。
