using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        };

        return JsonSerializer.Serialize(info, JsonOptions);
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

    /// <summary>
    /// 解析 B站 视频地址：取 DASH 音频 -> ffmpeg 转 mp3 -> 缓存，返回可直接播的 stream:// 地址。
    /// 阻塞时间取决于下载+转码（几分钟的视频约 5~20 秒），前端应显示“解析中”。
    /// 返回 JSON：{ ok, bvid, title, artist, duration, streamUrl, cached, error }
    /// </summary>
    public string ResolveBilibili(string url)
    {
        try
        {
            var track = BilibiliResolver
                .ResolveAsync(url ?? string.Empty, _form.Options.UserDataDir)
                .GetAwaiter()
                .GetResult();

            var streamUrl = StreamRegistry.RegisterLocalFile(track.Mp3Path);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                bvid = track.Bvid,
                title = track.Title,
                artist = track.Artist,
                duration = track.Duration,
                streamUrl,
                cached = track.FromCache,
                error = (string?)null,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                bvid = (string?)null,
                title = (string?)null,
                artist = (string?)null,
                duration = 0d,
                streamUrl = (string?)null,
                cached = false,
                error = ex.Message,
            }, JsonOptions);
        }
    }

    /// <summary>
    /// 解析 YouTube 地址：yt-dlp 取 bestaudio（webm/opus）→ ffmpeg 转 mp3 → 缓存，
    /// 返回可直接播的 stream:// 地址。返回 JSON 结构同 ResolveBilibili。
    /// </summary>
    public string ResolveYouTube(string url)
    {
        try
        {
            var proxy = _form.Options.Proxy ?? SystemProxy.Detect();
            var track = YoutubeResolver
                .ResolveAsync(url ?? string.Empty, _form.Options.UserDataDir, proxy)
                .GetAwaiter()
                .GetResult();

            var streamUrl = StreamRegistry.RegisterLocalFile(track.Mp3Path);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                bvid = track.VideoId,
                title = track.Title,
                artist = track.Artist,
                duration = track.Duration,
                streamUrl,
                cached = track.FromCache,
                error = (string?)null,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                bvid = (string?)null,
                title = (string?)null,
                artist = (string?)null,
                duration = 0d,
                streamUrl = (string?)null,
                cached = false,
                error = ex.Message,
            }, JsonOptions);
        }
    }

    /// <summary>
    /// 按关键词搜 B站 视频（关键词由前端把歌名 / 作者 / 专辑拼好后传进来）。
    /// 返回 JSON：{ ok, source, keyword, items[], error }
    /// 阻塞时间约 0.3~2 秒（wbi 签名 + 搜索接口），前端应显示“搜索中”。
    /// </summary>
    public string SearchBilibili(string keyword, int limit)
    {
        return Search("bilibili", keyword, limit);
    }

    /// <summary>
    /// 按关键词搜 YouTube 视频：yt-dlp 的 ytsearchN + -J --flat-playlist，
    /// 返回结构同 SearchBilibili。首次通常 3~10 秒（受网络/代理影响）。
    /// </summary>
    public string SearchYouTube(string keyword, int limit)
    {
        return Search("youtube", keyword, limit);
    }

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
