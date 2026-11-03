using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CefSharp;

namespace MusicPlayer.Host;

/// <summary>
/// 暴露给前端的 C# 接口，对应 renderer/src/bridge/host.ts 里的 CefHostApi。
/// CefSharp 会把方法名转成 camelCase：GetHostInfo -> getHostInfo。
/// </summary>
internal sealed class HostApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // 返回给前端的属性统一 camelCase（MediaSearchItem 这类强类型对象靠这个转换）
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MainForm _form;

    public HostApi(MainForm form)
    {
        _form = form;
    }

    /// <summary>返回 JSON 字符串，前端负责解析（避免跨进程类型映射问题）</summary>
    public string GetHostInfo()
    {
        var assembly = typeof(HostApi).Assembly;
        var info = new
        {
            appName = "音乐播放器（ver2 · app端）",
            hostVersion = assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            cefVersion = $"CefSharp {Cef.CefSharpVersion} / CEF {Cef.CefVersion}",
            chromiumVersion = Cef.ChromiumVersion,
            platform = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            dotnetVersion = RuntimeInformation.FrameworkDescription,
            isDev = _form.Options.Dev,
            userDataPath = _form.Options.UserDataDir,
            userDataSource = _form.Options.UserDataSource,
            // 诊断模式（--debug-ui）：前端据此打开 UI 事件时间线，详见 renderer/src/debug/uiTrace.ts
            isDebugUi = _form.Options.DebugUi,
            // 窗口 DPI 与客户区尺寸：和页面里的 devicePixelRatio 一起可以反推页面缩放
            deviceDpi = _form.DeviceDpi,
            clientWidth = _form.ClientSize.Width,
            clientHeight = _form.ClientSize.Height,
        };

        return JsonSerializer.Serialize(info, JsonOptions);
    }

    /// <summary>
    /// 设置面板的数据源：已保存的值、环境变量回退、实际生效的值，以及 Cookie 文件的体检结果。
    /// 前端只需渲染 JSON，不用自己拼优先级。
    /// </summary>
    public string GetSettings() => JsonSerializer.Serialize(BuildSettingsSnapshot(), JsonOptions);

    /// <summary>
    /// 数据目录信息（设置面板「数据目录」区块的数据源）：当前生效目录、来源、
    /// userdata.txt 位置、是否便携模式，以及是否被启动参数覆盖（覆盖了在面板里改就没用）。
    /// </summary>
    public string GetDataDirectory() => JsonSerializer.Serialize(new
    {
        ok = true,
        current = _form.Options.UserDataDir,
        source = _form.Options.UserDataSource,
        configPath = DataLocation.ConfigPath,
        configExists = File.Exists(DataLocation.ConfigPath),
        savedPath = DataLocation.ReadSavedPath() ?? string.Empty,
        portable = DataLocation.PortableEnabled,
        defaultPath = DataLocation.DefaultPath,
        overriddenBy = DataDirectoryOverride(),
    }, JsonOptions);

    /// <summary>
    /// 换数据目录：弹文件夹选择框 -> 校验可写 -> 写 userdata.txt。
    /// CEF 的 profile 路径在进程启动时就定了，所以必须重启才生效，返回值里带 restartRequired。
    /// </summary>
    public string ChooseDataDirectory()
    {
        try
        {
            var picked = ShowDataDirectoryDialog();
            if (string.IsNullOrWhiteSpace(picked))
            {
                // 用户取消：不是错误
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    canceled = true,
                    dir = string.Empty,
                    saved = false,
                    restartRequired = false,
                    warning = string.Empty,
                    error = string.Empty,
                }, JsonOptions);
            }

            var dir = DataLocation.Prepare(DataLocation.Normalize(picked));
            var saved = DataLocation.SavePath(dir);
            var overridden = DataDirectoryOverride();

            var warning = !string.IsNullOrEmpty(overridden)
                ? $"已记下 {dir}，但当前启动用的是{overridden}，重启后仍然会用它；要换目录请先去掉那个参数/变量。"
                : !saved
                    ? $"已切到 {dir}，但没能写进 {DataLocation.ConfigFileName}（exe 所在目录不可写），下次启动需要重新选。"
                    : string.Empty;

            PlaybackLog.Write("settings", "data_directory_changed", new
            {
                dir,
                saved,
                overriddenBy = overridden,
                restartRequired = true,
            });

            return JsonSerializer.Serialize(new
            {
                ok = true,
                canceled = false,
                dir,
                saved,
                restartRequired = true,
                warning,
                error = string.Empty,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("settings", "choose_data_directory_failed", ex);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                canceled = false,
                dir = string.Empty,
                saved = false,
                restartRequired = false,
                warning = string.Empty,
                error = ex.Message,
            }, JsonOptions);
        }
    }

    /// <summary>数据目录被启动参数覆盖时返回来源描述，否则空字符串（前端据此显示警告）</summary>
    private string DataDirectoryOverride()
    {
        var source = _form.Options.UserDataSource ?? string.Empty;
        if (source.StartsWith("命令行", StringComparison.Ordinal))
        {
            return "命令行参数 --user-data";
        }

        if (source.StartsWith("环境变量", StringComparison.Ordinal))
        {
            return "环境变量 MUSICPLAYER_USER_DATA";
        }

        return string.Empty;
    }

    /// <summary>FolderBrowserDialog 必须跑在 UI 线程；CEF 的 JS 调用线程要同步切回来拿结果。</summary>
    private string? ShowDataDirectoryDialog()
    {
        if (_form.IsDisposed)
        {
            return null;
        }

        string? selected = null;

        void Show()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择数据目录（播放列表、缓存、日志、设置都存在这里）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = _form.Options.UserDataDir,
            };

            if (dialog.ShowDialog(_form) == DialogResult.OK)
            {
                selected = dialog.SelectedPath;
            }
        }

        if (_form.InvokeRequired)
        {
            _form.Invoke((Action)Show);
        }
        else
        {
            Show();
        }

        return selected;
    }

    /// <summary>弹出系统文件选择框挑 Cookie 文件；用户取消时 path 为空。</summary>
    public string PickYouTubeCookiesFile()
    {
        try
        {
            var path = ShowCookiesFileDialog() ?? string.Empty;
            return JsonSerializer.Serialize(new { ok = true, path, error = string.Empty }, JsonOptions);
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("settings", "pick_cookies_failed", ex);
            return JsonSerializer.Serialize(new { ok = false, path = string.Empty, error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>只体检某个路径（不保存），用于「选择文件」后立刻告诉用户这个文件能不能用。</summary>
    public string CheckCookiesFile(string path) => JsonSerializer.Serialize(
        new { ok = true, cookieFile = DescribeCookieFile(CookieFileInspector.Inspect(path)) }, JsonOptions);

    /// <summary>
    /// 保存设置。入参 JSON：{ youtubeCookiesFile, youtubeCookiesBrowser, persistToUserEnvironment }。
    /// Cookie 文件不存在时返回 ok=false，不写入设置文件；保存后立即对下一次 yt-dlp 调用生效，无需重启。
    /// </summary>
    public string SaveSettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;

            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            var persist = root.TryGetProperty("persistToUserEnvironment", out var flag)
                && flag.ValueKind is JsonValueKind.True;

            var saved = PlayerSettings.Save(Text("youtubeCookiesFile"), Text("youtubeCookiesBrowser"));
            var warning = persist ? MirrorToUserEnvironment(saved) : string.Empty;
            PlaybackLog.Write("settings", "saved", new
            {
                cookieFile = saved.YouTubeCookiesFile,
                cookieBrowser = saved.YouTubeCookiesBrowser,
                persistToEnvironment = persist,
                warning,
            });

            return JsonSerializer.Serialize(
                new { ok = true, error = string.Empty, warning, settings = BuildSettingsSnapshot() }, JsonOptions);
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("settings", "save_failed", ex);
            return JsonSerializer.Serialize(
                new { ok = false, error = ex.Message, warning = string.Empty, settings = (object?)null }, JsonOptions);
        }
    }

    /// <summary>把 Cookie 文件同步到用户级环境变量，方便命令行工具复用；失败只给提示，不影响保存。</summary>
    private static string MirrorToUserEnvironment(PlayerSettings.SettingsData saved)
    {
        try
        {
            // null 表示删除该变量：清空 Cookie 文件时把之前写进去的值一起清掉。
            Environment.SetEnvironmentVariable(
                YtDlpAuthentication.CookieFileVariable,
                string.IsNullOrWhiteSpace(saved.YouTubeCookiesFile) ? null : saved.YouTubeCookiesFile,
                EnvironmentVariableTarget.User);
            return string.Empty;
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("settings", "mirror_environment_failed", ex);
            return $"设置已保存，但写入用户环境变量失败：{ex.Message}";
        }
    }

    private static object BuildSettingsSnapshot()
    {
        var saved = PlayerSettings.Read();
        var effectiveFile = PlayerSettings.EffectiveCookiesFile ?? string.Empty;
        var effectiveBrowser = PlayerSettings.EffectiveCookiesBrowser ?? string.Empty;
        var mode = effectiveFile.Length > 0 ? "file" : effectiveBrowser.Length > 0 ? "browser" : "none";
        var ytDlp = YtDlpLocator.Find();

        return new
        {
            ok = true,
            inHost = true,
            settingsPath = PlayerSettings.FilePath ?? string.Empty,
            // 设置文件里存的值（表单初值）
            ui = new
            {
                youtubeCookiesFile = saved.YouTubeCookiesFile ?? string.Empty,
                youtubeCookiesBrowser = saved.YouTubeCookiesBrowser ?? string.Empty,
            },
            // 环境变量里另配的值（设置优先，界面需要提示「这条其实没用上」）
            environment = new
            {
                youtubeCookiesFile = PlayerSettings.EnvironmentCookiesFile ?? string.Empty,
                youtubeCookiesBrowser = PlayerSettings.EnvironmentCookiesBrowser ?? string.Empty,
            },
            effective = new
            {
                mode,
                source = PlayerSettings.DescribeSource(cookiesFile: mode == "file"),
                youtubeCookiesFile = effectiveFile,
                youtubeCookiesBrowser = effectiveBrowser,
            },
            cookieFile = DescribeCookieFile(
                CookieFileInspector.Inspect(mode == "file" ? effectiveFile : null)),
            ytDlp = new
            {
                found = ytDlp != null,
                command = ytDlp is null
                    ? string.Empty
                    : string.Join(" ", ytDlp.PrefixArguments.Prepend(ytDlp.FileName)),
            },
            loadWarning = PlayerSettings.LoadWarning ?? string.Empty,
        };
    }

    private static object DescribeCookieFile(CookieFileInfo info) => new
    {
        path = info.FilePath,
        exists = info.Exists,
        size = info.Size,
        modified = info.Modified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
        netscape = info.Netscape,
        cookieCount = info.CookieCount,
        expiredCount = info.ExpiredCount,
        note = info.Note,
    };

    /// <summary>把整库播放列表 JSON 导出到用户选的文件（UTF-8 无 BOM）。</summary>
    public string ExportPlaylists(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return JsonSerializer.Serialize(
                    new { ok = false, canceled = false, path = string.Empty, error = "没有可导出的播放列表" }, JsonOptions);
            }

            var path = ShowPlaylistFileDialog(save: true);
            if (path is null)
            {
                return JsonSerializer.Serialize(
                    new { ok = true, canceled = true, path = string.Empty, error = string.Empty }, JsonOptions);
            }

            File.WriteAllText(path, json, new UTF8Encoding(false));
            PlaybackLog.Write("playlist", "exported", new { path, chars = json.Length });
            return JsonSerializer.Serialize(
                new { ok = true, canceled = false, path, error = string.Empty }, JsonOptions);
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("playlist", "export_failed", ex);
            return JsonSerializer.Serialize(
                new { ok = false, canceled = false, path = string.Empty, error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// 选择播放列表文件并读回文本（解析和校验在前端完成，宿主只负责文件 IO）。
    /// 返回 JSON：{ ok, canceled, path, fileName, text, error }
    /// </summary>
    public string ImportPlaylists()
    {
        const long maxBytes = 8 * 1024 * 1024;
        try
        {
            var path = ShowPlaylistFileDialog(save: false);
            if (path is null)
            {
                return JsonSerializer.Serialize(
                    new { ok = true, canceled = true, path = string.Empty, fileName = string.Empty, text = string.Empty, error = string.Empty },
                    JsonOptions);
            }

            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false, canceled = false, path, fileName = info.Name, text = string.Empty,
                    error = $"文件太大（{info.Length / 1024 / 1024} MB），播放列表文件一般只有几十 KB。",
                }, JsonOptions);
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            PlaybackLog.Write("playlist", "imported", new { path, chars = text.Length });
            return JsonSerializer.Serialize(
                new { ok = true, canceled = false, path, fileName = info.Name, text, error = string.Empty }, JsonOptions);
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("playlist", "import_failed", ex);
            return JsonSerializer.Serialize(new
            {
                ok = false, canceled = false, path = string.Empty, fileName = string.Empty, text = string.Empty,
                error = ex.Message,
            }, JsonOptions);
        }
    }

    /// <summary>导出用保存框、导入用打开框；必须在 UI 线程弹。</summary>
    private string? ShowPlaylistFileDialog(bool save)
    {
        if (_form.IsDisposed)
        {
            return null;
        }

        string? selected = null;
        var initialDirectory = PlayerSettings.DataDirectory is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

        void Show()
        {
            if (save)
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "导出播放列表",
                    Filter = "播放列表 (*.json)|*.json|所有文件 (*.*)|*.*",
                    FileName = $"音乐播放器-播放列表-{DateTime.Now:yyyyMMdd-HHmm}.json",
                    DefaultExt = "json",
                    AddExtension = true,
                    OverwritePrompt = true,
                    RestoreDirectory = true,
                };
                if (initialDirectory != null)
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                if (dialog.ShowDialog(_form) == DialogResult.OK)
                {
                    selected = dialog.FileName;
                }

                return;
            }

            using var open = new OpenFileDialog
            {
                Title = "导入播放列表",
                Filter = "播放列表 (*.json;*.txt)|*.json;*.txt|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };
            if (initialDirectory != null)
            {
                open.InitialDirectory = initialDirectory;
            }

            if (open.ShowDialog(_form) == DialogResult.OK)
            {
                selected = open.FileName;
            }
        }

        if (_form.InvokeRequired)
        {
            _form.Invoke((Action)Show);
        }
        else
        {
            Show();
        }

        return selected;
    }

    /// <summary>OpenFileDialog 必须跑在 UI 线程；CEF 的 JS 调用线程要 Invoke 回来。</summary>
    private string? ShowCookiesFileDialog()
    {
        if (_form.IsDisposed)
        {
            return null;
        }

        string? selected = null;

        void Show()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择 yt-dlp 使用的 Cookie 文件（Netscape 格式）",
                Filter = "Cookie 文件 (*.txt;*.cookies)|*.txt;*.cookies|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };

            var current = PlayerSettings.EffectiveCookiesFile;
            var initialDirectory = current is { Length: > 0 } ? Path.GetDirectoryName(current) : PlayerSettings.DataDirectory;
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            if (dialog.ShowDialog(_form) == DialogResult.OK)
            {
                selected = dialog.FileName;
            }
        }

        if (_form.InvokeRequired)
        {
            _form.Invoke((Action)Show);
        }
        else
        {
            Show();
        }

        return selected;
    }

    /// <summary>往返调用演示</summary>
    public string Echo(string text)
    {
        var payload = text ?? string.Empty;
        return $"C# 宿主已收到：\"{payload}\"\n长度 {payload.Length} 字符 · 宿主 PID {Environment.ProcessId}";
    }

    /// <summary>
    /// 注册一个由宿主代理的媒体地址：传入 http(s) 地址和可选的请求头 JSON，
    /// 返回可直接当 src 用的 stream://media/&lt;id&gt;。
    /// 用它来播 http 源（prod 下 secure origin 不能直接加载），或注入 Referer 等头。
    /// </summary>
    public string RegisterStream(string url, string headersJson)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return StreamRegistry.Register(url.Trim(), headersJson);
    }

    /// <summary>不依赖 CEF 媒体 error 事件，明确报告后台音轨读取/解码失败。</summary>
    public string GetStreamError(string url) => StreamRegistry.GetError(url);

    public void RecordPlaybackLog(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 24000) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement;
            var eventName = data.TryGetProperty("event", out var name) ? name.GetString() ?? "unknown" : "unknown";
            PlaybackLog.Write("renderer", eventName, data.TryGetProperty("data", out var details) ? details : default,
                data.TryGetProperty("level", out var level) ? level.GetString() ?? "info" : "info");
        }
        catch { }
    }

    public async Task OpenLogFolder()
    {
        await PlaybackLog.FlushAsync();
        if (string.IsNullOrEmpty(PlaybackLog.DirectoryPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{PlaybackLog.DirectoryPath}\"", UseShellExecute = true });
    }

    public string GetStreamFailure(string url) => JsonSerializer.Serialize(new
    {
        message = StreamRegistry.GetError(url), recoverable = StreamRegistry.IsRecoverable(url),
    }, JsonOptions);

    /// <summary>刷新原始来源并注册新流；恢复位置由播放器持有，不写音频文件。</summary>
    public string RecoverStream(string url, string supportedTypesJson)
    {
        PlaybackLog.Write("resolver", "recovery_requested", new { source = url });
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "stream" || uri.Host != "media"
                || !StreamRegistry.TryGet(uri.AbsolutePath.Trim('/'), out var descriptor))
                throw new InvalidOperationException("播放来源已失效，请重新选择歌曲。");
            var track = descriptor.Track;
            if (descriptor.LocalPath != null) throw new InvalidOperationException("本地文件无法加载，请检查文件是否存在。");
            if (track?.SourceKind == "bilibili") track = BilibiliResolver.ResolveAsync(track.OriginalUrl).GetAwaiter().GetResult();
            else if (track?.SourceKind == "youtube") track = YoutubeResolver.ResolveAsync(track.OriginalUrl, track.Proxy).GetAwaiter().GetResult();
            var next = track == null ? StreamRegistry.Register(descriptor.Url, JsonSerializer.Serialize(descriptor.Headers))
                : AudioStreamRegistration.Register(track, supportedTypesJson).Url;
            PlaybackLog.Write("resolver", "recovery_resolved", new { previousSource = url, source = next, trackId = track?.Id });
            return JsonSerializer.Serialize(new { url = next }, JsonOptions);
        }
        catch (Exception error)
        {
            // 平台临时风控和网络不可达可以等待；删除、权限限制及解码配置问题需明确报错。
            var recoverable = StreamRegistry.CanRecover(error) || error.Message.Contains("412")
                || error.Message.Contains("code=-500") || error.Message.Contains("超时")
                || error.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("Unable to download", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("Connection", StringComparison.OrdinalIgnoreCase);
            PlaybackLog.Exception("resolver", "recovery_failed", error, new { source = url, recoverable });
            return JsonSerializer.Serialize(new { error = ResolveMessage(error), recoverable }, JsonOptions);
        }
    }

    /// <summary>把解析异常转成界面上能看懂的一句话；完整异常仍然写进日志。</summary>
    private static string ResolveMessage(Exception error) => error switch
    {
        TaskCanceledException => "解析超时，请检查网络后重试",
        OperationCanceledException => "解析已取消，请重试",
        HttpRequestException => "网络请求失败，请检查网络后重试",
        _ => error.Message,
    };

    /// <summary>
    /// 解析某个站点的音轨（source 为 bilibili / youtube），按实际解码能力选择直接代理或实时转码；
    /// 不下载音频文件。未知站点返回明确的失败信封，不会被当成 YouTube。
    /// 返回 JSON：{ ok, bvid, title, artist, duration, streamUrl, cached, error }
    /// </summary>
    public string ResolveMedia(string source, string url, string supportedTypesJson = "[]")
    {
        if (!IsKnownSource(source))
        {
            return SerializeResolveFailure($"暂不支持的站点：{source}");
        }

        return source == "bilibili"
            ? ResolveBilibiliCore(url, supportedTypesJson)
            : ResolveYouTubeCore(url, supportedTypesJson);
    }

    /// <summary>B站：取 DASH 音轨，必要时实时转码</summary>
    private string ResolveBilibiliCore(string? url, string supportedTypesJson)
    {
        PlaybackLog.Write("resolver", "bilibili_start", new { trackId = BilibiliResolver.ExtractBvid(url ?? string.Empty) });
        try
        {
            var track = BilibiliResolver
                .ResolveAsync(url ?? string.Empty)
                .GetAwaiter()
                .GetResult();

            return SerializeResolveSuccess(track, AudioStreamRegistration.Register(track, supportedTypesJson));
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("resolver", "bilibili_failed", ex);
            return SerializeResolveFailure(ResolveMessage(ex));
        }
    }

    /// <summary>YouTube：yt-dlp 仅取流地址，不下载媒体文件</summary>
    private string ResolveYouTubeCore(string? url, string supportedTypesJson)
    {
        PlaybackLog.Write("resolver", "youtube_start");
        try
        {
            var proxy = _form.Options.Proxy ?? SystemProxy.Detect();
            var track = YoutubeResolver
                .ResolveAsync(url ?? string.Empty, proxy,
                    preferOpus: AudioStreamRegistration.SupportedTypes(supportedTypesJson).Contains("audio/webm; codecs=\"opus\""))
                .GetAwaiter()
                .GetResult();

            return SerializeResolveSuccess(track, AudioStreamRegistration.Register(track, supportedTypesJson));
        }
        catch (Exception ex)
        {
            PlaybackLog.Exception("resolver", "youtube_failed", ex);
            return SerializeResolveFailure(ResolveMessage(ex));
        }
    }

    private static string SerializeResolveSuccess(RemoteAudioTrack track, (string Url, string Mode) playback) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            bvid = track.Id,
            title = track.Title,
            artist = track.Artist,
            duration = track.Duration,
            streamUrl = playback.Url,
            playbackMode = playback.Mode,
            cached = false,
            error = (string?)null,
        }, JsonOptions);

    private static string SerializeResolveFailure(string message) => JsonSerializer.Serialize(new
    {
        ok = false,
        bvid = (string?)null,
        title = (string?)null,
        artist = (string?)null,
        duration = 0d,
        streamUrl = (string?)null,
        cached = false,
        error = message,
    }, JsonOptions);

    /// <summary>
    /// 读取某个站点的视频信息（标题、封面、作者），不解析音轨。
    /// 返回 JSON：{ ok, item, error }
    /// </summary>
    public string GetVideoInfo(string source, string url)
    {
        if (!IsKnownSource(source))
        {
            return JsonSerializer.Serialize(
                new { ok = false, item = (MediaSearchItem?)null, error = $"暂不支持的站点：{source}" }, JsonOptions);
        }

        try
        {
            var item = source == "bilibili"
                ? BilibiliResolver.GetVideoInfoAsync(url ?? string.Empty).GetAwaiter().GetResult()
                : YoutubeResolver
                    .GetVideoInfoAsync(url ?? string.Empty, _form.Options.Proxy ?? SystemProxy.Detect())
                    .GetAwaiter()
                    .GetResult();
            return JsonSerializer.Serialize(new { ok = true, item, error = (string?)null }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, item = (MediaSearchItem?)null, error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// 按关键词搜某个站点的视频（关键词由前端把歌名 / 作者 / 专辑拼好后传进来）。
    /// 返回 JSON：{ ok, source, keyword, items[], error }
    /// </summary>
    public string SearchMedia(string source, string keyword, int limit)
    {
        return IsKnownSource(source)
            ? Search(source, keyword, limit)
            : SerializeSearch(source, (keyword ?? string.Empty).Trim(), Array.Empty<MediaSearchItem>(),
                $"暂不支持的站点：{source}");
    }

    private static bool IsKnownSource(string? source) => source is "bilibili" or "youtube";

    /// <summary>用系统默认浏览器打开外链（B站 / YouTube 视频页）。只放行 http/https，避免被当成启动本地程序。</summary>
    public void OpenExternalUrl(string url)
    {
        if (!TryGetWebUrl(url, out var absolute))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = absolute,
            UseShellExecute = true,
        });
    }

    private string Search(string source, string keyword, int limit)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return SerializeSearch(source, text, Array.Empty<MediaSearchItem>(), "请先填歌曲名（作者、专辑可以留空）。");
        }

        try
        {
            IReadOnlyList<MediaSearchItem> items = source == "bilibili"
                ? BilibiliSearch.SearchAsync(text, limit).GetAwaiter().GetResult()
                : YoutubeSearch
                    .SearchAsync(text, limit, _form.Options.Proxy ?? SystemProxy.Detect())
                    .GetAwaiter()
                    .GetResult();

            return SerializeSearch(source, text, items, null);
        }
        catch (Exception ex)
        {
            return SerializeSearch(source, text, Array.Empty<MediaSearchItem>(), ex.Message);
        }
    }

    private static string SerializeSearch(
        string source,
        string keyword,
        IReadOnlyList<MediaSearchItem> items,
        string? error)
    {
        return JsonSerializer.Serialize(new
        {
            ok = error == null,
            source,
            keyword,
            items,
            error,
        }, JsonOptions);
    }

    private static bool TryGetWebUrl(string? raw, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        absolute = uri.AbsoluteUri;
        return true;
    }

    public void Minimize()
    {
        OnUiThread(() => _form.WindowState = FormWindowState.Minimized);
    }

    public void ToggleMaximize()
    {
        OnUiThread(() =>
            _form.WindowState = _form.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized);
    }

    public void CloseWindow()
    {
        OnUiThread(() => _form.Close());
    }

    public void OpenDevTools()
    {
        OnUiThread(() => _form.Browser.ShowDevTools());
    }

    public void SetZoomLevel(double level)
    {
        OnUiThread(() => _form.Browser.SetZoomLevel(level));
    }

    public void OpenUserDataFolder()
    {
        var dir = _form.Options.UserDataDir;
        OnUiThread(() => Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dir}\"",
            UseShellExecute = true,
        }));
    }

    /// <summary>CEF 的 JS 回调不在 WinForms UI 线程上，改窗口状态必须切回去</summary>
    private void OnUiThread(Action action)
    {
        if (_form.IsDisposed)
        {
            return;
        }

        if (_form.InvokeRequired)
        {
            _form.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
