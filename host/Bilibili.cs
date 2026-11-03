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

    private static async Task<JsonElement> GetVideoDataAsync(string input, CancellationToken cancellationToken)
    {
        var bvid = ExtractBvid(input) ?? throw new InvalidOperationException("没找到 BV 号，请粘贴视频地址或 BV 号。");

        // 1. 视频信息
        var viewJson = await GetJsonAsync($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", cancellationToken);
        var viewCode = viewJson.GetProperty("code").GetInt32();
        if (viewCode != 0)
        {
            if (viewCode == -412) throw BilibiliHttp.Blocked("取视频信息失败");
            throw new InvalidOperationException($"取视频信息失败：code={viewCode} {viewJson.GetProperty("message").GetString()}");
        }

        return viewJson.GetProperty("data");
    }

    /// <summary>读取视频标题、封面和作者，不解析音轨或注册播放流。</summary>
    public static async Task<MediaSearchItem> GetVideoInfoAsync(string input, CancellationToken cancellationToken = default)
    {
        var data = await GetVideoDataAsync(input, cancellationToken);
        return ParseVideoInfo(data, input);
    }

    internal static MediaSearchItem ParseVideoInfo(JsonElement data, string input)
    {
        var bvid = SearchJson.ReadString(data, "bvid");
        if (string.IsNullOrEmpty(bvid)) bvid = ExtractBvid(input) ?? throw new InvalidOperationException("视频信息缺少 BV 号。");
        var title = SearchJson.ReadString(data, "title");
        if (string.IsNullOrWhiteSpace(title)) title = bvid;
        var artist = data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
            ? SearchJson.ReadString(owner, "name") : string.Empty;
        var cover = SearchJson.ReadString(data, "pic");
        if (cover.StartsWith("//", StringComparison.Ordinal)) cover = "https:" + cover;
        if (cover.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) cover = "https://" + cover[7..];
        var playCount = data.TryGetProperty("stat", out var stat) && stat.ValueKind == JsonValueKind.Object
            ? SearchJson.ReadLong(stat, "view") : 0;
        return new MediaSearchItem("bilibili", bvid, title, artist, input, cover,
            SearchJson.ReadDouble(data, "duration"), playCount);
    }

    /// <summary>只解析视频信息和音轨，不下载或缓存音频</summary>
    public static async Task<RemoteAudioTrack> ResolveAsync(string input, CancellationToken cancellationToken = default)
    {
        var bvid = ExtractBvid(input) ?? throw new InvalidOperationException("没找到 BV 号，请粘贴视频地址或 BV 号。");
        var viewData = await GetVideoDataAsync(input, cancellationToken);
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

        if (playJson.GetProperty("data").TryGetProperty("timelength", out var timeLength) && timeLength.TryGetDouble(out var milliseconds))
            duration = milliseconds / 1000;

        var codec = picked.TryGetProperty("codecs", out var codecValue) ? codecValue.GetString() : "";
        return new RemoteAudioTrack(bvid, title, artist, duration, audioUrl,
            codec == "mp4a.40.2" ? "audio/mp4; codecs=\"mp4a.40.2\"" : "", new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = "https://www.bilibili.com/",
            }, null) { SourceKind = "bilibili", OriginalUrl = input };
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

        // 便携包：exe 同目录 / exe\bin 里的 ffmpeg.exe
        var bundled = BundledTools.FindLocal("ffmpeg.exe");
        if (bundled != null)
        {
            return bundled;
        }

        // PATH
        var onPath = BundledTools.FindOnPath("ffmpeg.exe");
        if (onPath != null)
        {
            return onPath;
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
