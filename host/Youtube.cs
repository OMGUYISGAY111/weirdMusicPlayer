using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Host;

/// <summary>
/// YouTube 音频解析：用 yt-dlp 取 bestaudio（通常是 webm/opus）-> ffmpeg 转 mp3 -> 落地缓存。
///
/// 为什么走 yt-dlp 而不是 iframe：
///   - iframe 方案要求页面 origin 是正常 web origin，且字幕/播放器资源在代理不稳时经常加载不全；
///   - yt-dlp 一次成功取流即得到完整音频文件，转码后复用 B站 那条 stream:// 本地播放链路，
///     播放/seek/缓冲行为和本地文件完全一致。
///
/// 代理：Python 的 urllib 不读 Windows 系统代理，所以要显式把 --proxy 传给 yt-dlp
/// （取 --proxy 参数、MUSICPLAYER_PROXY 环境变量，或注册表里的 WinINET 代理）。
/// </summary>
internal sealed record YoutubeTrack(string VideoId, string Title, string Artist, double Duration, string Mp3Path, bool FromCache);

internal static class YoutubeResolver
{
    public static async Task<YoutubeTrack> ResolveAsync(
        string input,
        string cacheRoot,
        string? proxy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("请填 YouTube 视频地址。");
        }

        var cacheDir = Path.Combine(cacheRoot, "youtube");
        Directory.CreateDirectory(cacheDir);

        var index = LoadIndex(cacheDir);
        var cacheKey = NormalizeKey(input);

        if (index.TryGetValue(cacheKey, out var cached) && File.Exists(cached.Path) && new FileInfo(cached.Path).Length > 1024)
        {
            return new YoutubeTrack(cached.VideoId, cached.Title, cached.Artist, cached.Duration, cached.Path, true);
        }

        var ytDlp = YtDlpLocator.Find()
            ?? throw new InvalidOperationException(
                "找不到 yt-dlp。安装方式（任选其一）：\n" +
                "  · pip install --user yt-dlp（或用镜像：-i https://mirrors.aliyun.com/pypi/simple/）\n" +
                "  · 放一个 yt-dlp.exe 到 PATH，或用环境变量 MUSICPLAYER_YTDLP 指定路径");

        var ffmpeg = FfmpegLocator.Find()
            ?? throw new InvalidOperationException("找不到 ffmpeg：YouTube 音频需要转成 mp3 才能播放（winget install Gyan.FFmpeg）。");

        var outputTemplate = Path.Combine(cacheDir, "%(id)s.%(ext)s");
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlp.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 强制 UTF-8：否则中文标题会按控制台代码页解码成乱码（搜索结果页同样处理）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var argument in ytDlp.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 一次跑完：下载 + 转 mp3，并把元数据和最终路径打印出来
        foreach (var argument in new[]
        {
            "--no-playlist",
            "--no-progress",
            "--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            startInfo.ArgumentList.Add("--proxy");
            startInfo.ArgumentList.Add(proxy);
        }

        YtDlpAuthentication.Apply(startInfo);
        YtDlpRuntime.Apply(startInfo);

        foreach (var argument in new[]
        {
            "-f", YtDlpRuntime.AudioFormat,
            "-x",
            "--audio-format", "mp3",
            "-o", outputTemplate,
            "--print", "after_move:filepath",
            "--print", "MYID:%(id)s",
            "--print", "MYTITLE:%(title)s",
            "--print", "MYARTIST:%(uploader)s",
            "--print", "MYDURATION:%(duration)s",
            input.Trim(),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("yt-dlp 启动失败。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(YtDlpAuthentication.DescribeFailure(stderr, process.ExitCode));
        }

        var lines = stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var filePath = lines.FirstOrDefault(line => line.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
        var videoId = ExtractValue(lines, "MYID:") ?? "unknown";
        var title = ExtractValue(lines, "MYTITLE:") ?? videoId;
        var artist = ExtractValue(lines, "MYARTIST:") ?? string.Empty;
        var duration = double.TryParse(ExtractValue(lines, "MYDURATION:"), out var seconds) ? seconds : 0;

        if (filePath == null || !File.Exists(filePath))
        {
            throw new InvalidOperationException($"yt-dlp 没有产出 mp3（输出：{Trim(stdout, 400)}）");
        }

        index[cacheKey] = new CachedEntry(videoId, title, artist, duration, filePath);
        SaveIndex(cacheDir, index);

        return new YoutubeTrack(videoId, title, artist, duration, filePath, false);
    }

    private sealed record CachedEntry(string VideoId, string Title, string Artist, double Duration, string Path);

    private static string NormalizeKey(string input)
    {
        var match = Regex.Match(input, "(?:v=|youtu\\.be/|/shorts/|/embed/)([A-Za-z0-9_-]{6,})");
        if (match.Success)
        {
            return "yt:" + match.Groups[1].Value;
        }

        return "url:" + input.Trim().ToLowerInvariant();
    }

    private static string? ExtractValue(IEnumerable<string> lines, string prefix)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static string Trim(string text, int max)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    private static string IndexPath(string cacheDir) => Path.Combine(cacheDir, "index.json");

    private static Dictionary<string, CachedEntry> LoadIndex(string cacheDir)
    {
        try
        {
            var path = IndexPath(cacheDir);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, CachedEntry>>(File.ReadAllText(path))
                    ?? new Dictionary<string, CachedEntry>();
            }
        }
        catch
        {
            // 索引坏了就重建
        }

        return new Dictionary<string, CachedEntry>();
    }

    private static void SaveIndex(string cacheDir, Dictionary<string, CachedEntry> index)
    {
        try
        {
            File.WriteAllText(IndexPath(cacheDir), JsonSerializer.Serialize(index));
        }
        catch
        {
            // 忽略
        }
    }
}

internal sealed record YtDlpCommand(string FileName, IReadOnlyList<string> PrefixArguments);

internal static class YtDlpRuntime
{
    // 优先独立音轨；只有音视频合并格式时提取其中的音频。
    internal const string AudioFormat = "bestaudio/best[acodec!=none]";

    internal static void Apply(ProcessStartInfo startInfo)
    {
        var configured = Environment.GetEnvironmentVariable("MUSICPLAYER_NODE");
        var node = string.IsNullOrWhiteSpace(configured)
            ? YtDlpLocator.FindOnPath("node.exe")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        if (!string.IsNullOrWhiteSpace(configured) && !File.Exists(node))
            throw new InvalidOperationException("MUSICPLAYER_NODE 指定的 Node.js 程序不存在。");
        foreach (var argument in BuildArguments(node)) startInfo.ArgumentList.Add(argument);
    }

    internal static IReadOnlyList<string> BuildArguments(string? node) => string.IsNullOrWhiteSpace(node)
        ? Array.Empty<string>() // 未找到 Node 时仍允许 yt-dlp 使用默认的 Deno 运行时。
        : new[] { "--js-runtimes", $"node:{node}" };
}

/// <summary>只使用用户显式配置的登录来源；不自动扫描或提取浏览器 Cookie。</summary>
internal static class YtDlpAuthentication
{
    internal const string CookieFileVariable = "MUSICPLAYER_YOUTUBE_COOKIES";
    internal const string BrowserVariable = "MUSICPLAYER_YOUTUBE_COOKIES_BROWSER";

    internal static void Apply(ProcessStartInfo startInfo)
    {
        foreach (var argument in BuildArguments(
            Environment.GetEnvironmentVariable(CookieFileVariable),
            Environment.GetEnvironmentVariable(BrowserVariable)))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string? cookieFile, string? browser)
    {
        if (!string.IsNullOrWhiteSpace(cookieFile))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cookieFile.Trim()));
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"找不到 YouTube Cookie 文件，请检查 {CookieFileVariable} 指定的路径。");
            return new[] { "--cookies", path };
        }

        // 浏览器/profile 选择交给 yt-dlp 解析，通过 ArgumentList 原样传递，不经过 shell。
        return string.IsNullOrWhiteSpace(browser)
            ? Array.Empty<string>()
            : new[] { "--cookies-from-browser", browser.Trim() };
    }

    internal static string DescribeFailure(string stderr, int exitCode)
    {
        if (stderr.Contains("Signature solving failed", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("n challenge solving failed", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube 签名解析失败，未能取得可用音频格式。请确认安装 Node.js 22+，" +
                "并为实际使用的 yt-dlp 安装配套依赖（Python 安装可运行 python -m pip install --user -U \"yt-dlp[default]\"）。" +
                "播放器会自动启用 PATH 中的 node.exe，也可通过 MUSICPLAYER_NODE 指定路径。";
        }

        if (stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube 要求登录或人机验证。请在浏览器中使用与播放器相同的代理打开此视频，" +
                "手动登录并完成验证。然后将导出的 Netscape 格式 Cookie 文件路径配置到 " +
                CookieFileVariable + "；或将浏览器名称（如 firefox、edge、chrome）配置到 " +
                BrowserVariable + "，重启播放器后重试。若已配置 Cookie，请检查是否过期、是否选错浏览器配置，" +
                "以及浏览器与播放器是否使用同一代理出口。";
        }

        if (stderr.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            && (stderr.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not copy", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)))
        {
            return "yt-dlp 无法读取或解密浏览器 Cookie。请先完全退出所选浏览器后重试；" +
                "若仍失败，改用导出的 Netscape 格式 Cookie 文件，并通过 " + CookieFileVariable + " 配置。";
        }

        if (stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube 未返回可用音频格式。播放器已尝试独立音轨及含音频的视频格式；" +
                "请用相同 Cookie、代理及 --js-runtimes node 执行 yt-dlp --list-formats 检查。" +
                "若只有 storyboard 图片，需继续检查签名解析依赖或平台限制。";
        }

        return $"yt-dlp 失败（exit {exitCode}）：{SearchJson.Shorten(stderr, 600)}";
    }
}

internal static class YtDlpLocator
{
    public static YtDlpCommand? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MUSICPLAYER_YTDLP");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return new YtDlpCommand(fromEnv, Array.Empty<string>());
        }

        // PATH 上的 yt-dlp.exe
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), "yt-dlp.exe");
                if (File.Exists(candidate))
                {
                    return new YtDlpCommand(candidate, Array.Empty<string>());
                }
            }
            catch
            {
                // 忽略非法路径片段
            }
        }

        // pip --user 安装后的脚本目录
        var userScripts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Python", "Python313", "Scripts", "yt-dlp.exe");
        if (File.Exists(userScripts))
        {
            return new YtDlpCommand(userScripts, Array.Empty<string>());
        }

        // python -m yt_dlp
        var python = FindOnPath("python.exe");
        if (python != null)
        {
            return new YtDlpCommand(python, new[] { "-m", "yt_dlp" });
        }

        return null;
    }

    internal static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 忽略
            }
        }

        return null;
    }
}

/// <summary>没有显式 --proxy 时，从注册表读 WinINET 代理（Clash 之类常见配置）</summary>
internal static class SystemProxy
{
    public static string? Detect()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null)
            {
                return null;
            }

            var enabled = key.GetValue("ProxyEnable") is int value && value == 1;
            var server = key.GetValue("ProxyServer") as string;
            if (!enabled || string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            // 可能是 "http=127.0.0.1:7897;https=..." 或 "127.0.0.1:7897"
            var httpPart = server.Split(';')
                .FirstOrDefault(part => part.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
            if (httpPart != null)
            {
                return "http://" + httpPart["http=".Length..];
            }

            return server.Contains('=') ? null : "http://" + server;
        }
        catch
        {
            return null;
        }
    }
}
