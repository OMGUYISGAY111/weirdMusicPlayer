using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Host;

/// <summary>
/// B站音频解析：视频页地址 -> 音频流 -> 转成 mp3 落地缓存。
///
/// 为什么必须转码：B站 DASH 音频是 AAC（m4s / audio/mp4，codec mp4a.40.2），
/// 当前 CEF 官方构建不含 AAC 解码器，直接喂给 &lt;audio&gt; 会 "no supported sources"。
/// 流程：wbi 签名调 playurl 拿 30280（192K AAC）-> 下载 m4s -> ffmpeg 转 mp3 -> 缓存复用。
///
/// 也不需要登录：nav 接口在未登录时同样返回 wbi_img（用于签名），
/// playurl 未登录也能拿到 192K 音频；要更高码率（Hi-Res / 杜比）才需要 SESSDATA。
/// </summary>
internal sealed record BilibiliTrack(
    string Bvid,
    string Title,
    string Artist,
    double Duration,
    string Mp3Path,
    bool FromCache);

/// <summary>B站 API 与音频 CDN 使用国内直连，不继承供 YouTube 使用的系统代理。</summary>
internal static class BilibiliHttp
{
    internal static HttpClientHandler CreateHandler(bool useCookies) => new()
    {
        UseProxy = false,
        UseCookies = useCookies,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    internal static InvalidOperationException Blocked(string stage) => new(
        $"{stage}：B站暂时拦截了请求（-412/412）。当前 B站请求已使用直连；" +
        "若开启了 VPN/TUN 全局代理，请将 bilibili.com 和 bilivideo.com 设置为直连，稍后重试。");
}

internal static class BilibiliResolver
{
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>wbi 签名的字符重排表（B站前端固定表）</summary>
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19,
        29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = BilibiliHttp.CreateHandler(useCookies: true);

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    /// <summary>从任意文本里抓 BV 号（支持整段 URL / 直接给 BV 号）</summary>
    public static string? ExtractBvid(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = Regex.Match(input, "BV[0-9A-Za-z]{10}", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    /// <summary>解析 + 转码，返回本地 mp3 路径（同一 BV 命中缓存直接复用）</summary>
    public static async Task<BilibiliTrack> ResolveAsync(string input, string cacheRoot, CancellationToken cancellationToken = default)
    {
        var bvid = ExtractBvid(input) ?? throw new InvalidOperationException("没找到 BV 号，请粘贴视频地址或 BV 号。");

        var cacheDir = Path.Combine(cacheRoot, "bili");
        Directory.CreateDirectory(cacheDir);
        var mp3Path = Path.Combine(cacheDir, $"{bvid}.mp3");
        var metaPath = Path.Combine(cacheDir, $"{bvid}.json");

        if (File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 1024 && File.Exists(metaPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CachedMeta>(await File.ReadAllTextAsync(metaPath, cancellationToken));
                if (cached != null)
                {
                    return new BilibiliTrack(bvid, cached.Title, cached.Artist, cached.Duration, mp3Path, true);
                }
            }
            catch
            {
                // 缓存元数据坏了就重新解析
            }
        }

        // 1. 视频信息
        var viewJson = await GetJsonAsync($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", cancellationToken);
        var viewCode = viewJson.GetProperty("code").GetInt32();
        if (viewCode != 0)
        {
            if (viewCode == -412) throw BilibiliHttp.Blocked("取视频信息失败");
            throw new InvalidOperationException($"取视频信息失败：code={viewCode} {viewJson.GetProperty("message").GetString()}");
        }

        var viewData = viewJson.GetProperty("data");
        var cid = viewData.GetProperty("cid").GetInt64();
        var title = viewData.GetProperty("title").GetString() ?? bvid;
        var artist = viewData.TryGetProperty("owner", out var owner) && owner.TryGetProperty("name", out var ownerName)
            ? ownerName.GetString() ?? string.Empty
            : string.Empty;
        var duration = viewData.TryGetProperty("duration", out var durationProp) ? durationProp.GetDouble() : 0;

        // 2. wbi 签名拿播放地址
        var mixinKey = await GetMixinKeyAsync(cancellationToken);
        var query = BuildSignedQuery(new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1",
        }, mixinKey);

        var playJson = await GetJsonAsync($"https://api.bilibili.com/x/player/wbi/playurl?{query}", cancellationToken);
        var playCode = playJson.GetProperty("code").GetInt32();
        if (playCode != 0)
        {
            if (playCode == -412) throw BilibiliHttp.Blocked("取播放地址失败");
            throw new InvalidOperationException($"取播放地址失败：code={playCode} {playJson.GetProperty("message").GetString()}");
        }

        var audios = playJson.GetProperty("data").GetProperty("dash").GetProperty("audio").EnumerateArray().ToList();
        if (audios.Count == 0)
        {
            throw new InvalidOperationException("这个视频没有 DASH 音频流（可能是纯视频/直播）。");
        }

        // 30280 = 192K AAC，优先；否则取码率最高的
        var picked = audios.FirstOrDefault(item => item.GetProperty("id").GetInt32() == 30280);
        if (picked.ValueKind == JsonValueKind.Undefined)
        {
            picked = audios.OrderByDescending(item => item.GetProperty("bandwidth").GetInt64()).First();
        }

        var audioUrl = picked.GetProperty("baseUrl").GetString()
            ?? throw new InvalidOperationException("播放地址为空。");

        // 3. 下载 m4s（带 Referer/UA，B站 CDN 有防盗链）
        var m4sPath = Path.Combine(cacheDir, $"{bvid}.m4s");
        await DownloadAsync(audioUrl, m4sPath, cancellationToken);

        // 4. ffmpeg 转 mp3
        try
        {
            await TranscodeAsync(m4sPath, mp3Path, cancellationToken);
        }
        finally
        {
            TryDelete(m4sPath);
        }

        await File.WriteAllTextAsync(
            metaPath,
            JsonSerializer.Serialize(new CachedMeta(title, artist, duration)),
            cancellationToken);

        return new BilibiliTrack(bvid, title, artist, duration, mp3Path, false);
    }

    private sealed record CachedMeta(string Title, string Artist, double Duration);

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if ((int)response.StatusCode == 412) throw BilibiliHttp.Blocked("B站接口请求失败");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    internal static async Task<string> GetMixinKeyAsync(CancellationToken cancellationToken)
    {
        var nav = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", cancellationToken);
        if (!nav.TryGetProperty("data", out var data) || !data.TryGetProperty("wbi_img", out var wbi))
        {
            throw new InvalidOperationException("拿不到 wbi 签名用的 key（接口返回异常）。");
        }

        var imgKey = Path.GetFileNameWithoutExtension(wbi.GetProperty("img_url").GetString() ?? string.Empty);
        var subKey = Path.GetFileNameWithoutExtension(wbi.GetProperty("sub_url").GetString() ?? string.Empty);
        var raw = imgKey + subKey;
        if (raw.Length < 64)
        {
            throw new InvalidOperationException("wbi key 长度异常，接口结构可能变了。");
        }

        return new string(MixinKeyEncTab.Select(index => raw[index]).ToArray());
    }

    internal static string BuildSignedQuery(Dictionary<string, string> parameters, string mixinKey)
    {
        parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var query = string.Join("&", parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                // B站规则：先过滤 !'()* 再做 urlencode
                var value = Regex.Replace(pair.Value, "[!'()*]", string.Empty);
                return $"{pair.Key}={Uri.EscapeDataString(value)}";
            }));

        var wbiSignature = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey))).ToLowerInvariant();

        return $"{query}&w_rid={wbiSignature}";
    }

    private static async Task DownloadAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 412) throw BilibiliHttp.Blocked("下载音频失败");
            throw new InvalidOperationException($"下载音频失败：HTTP {(int)response.StatusCode}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var ffmpeg = FfmpegLocator.Find()
            ?? throw new InvalidOperationException(
                "找不到 ffmpeg：B站 音频是 AAC，需要 ffmpeg 转成 mp3。\n" +
                "可以安装 ffmpeg（winget install Gyan.FFmpeg）或用环境变量 MUSICPLAYER_FFMPEG 指定路径。");

        var tempOutput = outputPath + ".tmp.mp3";
        TryDelete(tempOutput);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("libmp3lame");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("192k");
        startInfo.ArgumentList.Add(tempOutput);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg 启动失败。");

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || !File.Exists(tempOutput))
        {
            throw new InvalidOperationException($"ffmpeg 转码失败（exit {process.ExitCode}）：{stderr.Trim()}");
        }

        TryDelete(outputPath);
        File.Move(tempOutput, outputPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }
}

internal static class FfmpegLocator
{
    public static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MUSICPLAYER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        // PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径片段，跳过
            }
        }

        // WinGet 安装的常见位置
        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        if (Directory.Exists(wingetRoot))
        {
            try
            {
                foreach (var package in Directory.EnumerateDirectories(wingetRoot, "Gyan.FFmpeg*"))
                {
                    var found = Directory.EnumerateFiles(package, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null)
                    {
                        return found;
                    }
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
