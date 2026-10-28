using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Host;

/// <summary>
/// 按关键词（歌名 + 作者 + 专辑）在 B站 / YouTube 搜视频，返回视频页链接，供前端列表展示和播放。
///
/// 为什么放在宿主里做：页面 origin 是 <c>app://</c>（prod）或 <c>http://127.0.0.1:5173</c>（dev），
/// CSP 的 connect-src 只放行本机，直接 fetch 两个平台会被 CORS / CSP 拦住；
/// 宿主进程没有这些限制，还能复用已有的 wbi 签名工具、yt-dlp 和系统代理配置。
///
///   · B站：web 搜索接口 search/type（wbi 签名 + buvid3/buvid4 指纹 cookie，未登录可用）
///   · YouTube：yt-dlp 的 ytsearchN: 伪地址 + -J --flat-playlist 拿结构化 JSON（不抓 HTML）
/// </summary>
internal sealed record MediaSearchItem(
    string Source,
    string Id,
    string Title,
    string Author,
    string Url,
    string Thumbnail,
    double Duration,
    long PlayCount);

/// <summary>两个平台搜索结果共用的 JSON 读取小工具（接口字段经常缺，统一兜底）</summary>
internal static class SearchJson
{
    public static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    public static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }

    public static double ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : 0;
    }

    public static string Shorten(string text, int max)
    {
        var single = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }
}

internal static class BilibiliSearch
{
    private const string SpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";
    private const string SearchUrl = "https://api.bilibili.com/x/web-interface/wbi/search/type";

    /// <summary>搜索结果标题带 &lt;em class="keyword"&gt; 高亮标签，展示前删掉</summary>
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// 搜索专用 HttpClient：UA / Referer 和 B站 解析器保持一致，但关掉自动 cookie，
    /// 指纹 cookie 手动放进请求头，避免和 CookieContainer 里已存的 buvid 重复。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = BilibiliHttp.CreateHandler(useCookies: false);

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BilibiliResolver.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    /// <summary>按关键词搜视频；未登录可用，命中风控（-412 / -352）时换一次指纹重试。</summary>
    public static async Task<IReadOnlyList<MediaSearchItem>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return Array.Empty<MediaSearchItem>();
        }

        limit = Math.Clamp(limit, 1, 30);

        var cookie = await TryGetFingerprintCookieAsync(cancellationToken);
        var json = await RequestAsync(text, limit, cookie, cancellationToken);
        var code = ReadCode(json);

        // 风控：-412（请求被拦截）/ -352（风控校验失败）。换一个新的 buvid 指纹再来一次。
        if (cookie != null && code is -412 or -352)
        {
            cookie = await TryGetFingerprintCookieAsync(cancellationToken);
            json = await RequestAsync(text, limit, cookie, cancellationToken);
            code = ReadCode(json);
        }

        if (code != 0)
        {
            var message = SearchJson.ReadString(json, "message");
            throw new InvalidOperationException(
                $"B站搜索失败（code={code}）{(string.IsNullOrEmpty(message) ? string.Empty : "：" + message)}");
        }

        var items = Parse(json, limit);
        if (items.Count == 0)
        {
            ThrowIfChallenge(json);
        }

        return items;
    }

    /// <summary>
    /// 被风控时接口仍然 code=0，但 data 里只有一个 v_voucher（要求人机验证）。
    /// 这种情况给明确提示，别让用户误以为是“歌名打错了”。
    /// </summary>
    private static void ThrowIfChallenge(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (data.TryGetProperty("v_voucher", out var voucher)
            && voucher.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(voucher.GetString()))
        {
            throw new InvalidOperationException(
                "B站 触发了风控校验（要求人机验证）。等十几秒再搜一次；" +
                "或者在浏览器里打开 bilibili.com 手动过一次验证后再试。");
        }
    }

    private static int ReadCode(JsonElement json)
    {
        return json.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : -1;
    }

    private static async Task<JsonElement> RequestAsync(
        string keyword,
        int limit,
        string? cookie,
        CancellationToken cancellationToken)
    {
        var mixinKey = await BilibiliResolver.GetMixinKeyAsync(cancellationToken);
        var query = BilibiliResolver.BuildSignedQuery(new Dictionary<string, string>
        {
            ["keyword"] = keyword,
            ["search_type"] = "video",
            ["order"] = "totalrank",
            ["page"] = "1",
            ["page_size"] = limit.ToString(CultureInfo.InvariantCulture),
        }, mixinKey);

        return await GetJsonAsync($"{SearchUrl}?{query}", cookie, cancellationToken);
    }

    /// <summary>
    /// 搜索接口对未登录请求也会做指纹校验，带上 buvid3/buvid4 能明显降低 -412 概率。
    /// 拿不到指纹不算失败：退回无 cookie 请求，接口通常也放行。
    /// </summary>
    private static async Task<string?> TryGetFingerprintCookieAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetJsonAsync(SpiUrl, null, cancellationToken);
            if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var b3 = SearchJson.ReadString(data, "b_3");
            var b4 = SearchJson.ReadString(data, "b_4");
            if (b3.Length == 0)
            {
                return null;
            }

            return b4.Length == 0 ? $"buvid3={b3}" : $"buvid3={b3}; buvid4={b4}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[bili-search] 取指纹失败，改用无 cookie 搜索：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonElement> GetJsonAsync(
        string url,
        string? cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"B站搜索接口 HTTP {(int)response.StatusCode}：{SearchJson.Shorten(text, 200)}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>把 search/type 的响应转成列表（internal 便于用真实响应单独验证解析）</summary>
    internal static IReadOnlyList<MediaSearchItem> Parse(JsonElement json, int limit)
    {
        var items = new List<MediaSearchItem>();
        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            // 没有命中时接口不返回 result 字段
            return items;
        }

        foreach (var entry in result.EnumerateArray())
        {
            if (items.Count >= limit)
            {
                break;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var bvid = SearchJson.ReadString(entry, "bvid");
            if (bvid.Length == 0)
            {
                continue;
            }

            var title = CleanTitle(SearchJson.ReadString(entry, "title"));
            var author = SearchJson.ReadString(entry, "author");
            var thumbnail = SearchJson.ReadString(entry, "pic");
            if (thumbnail.StartsWith("//", StringComparison.Ordinal))
            {
                thumbnail = "https:" + thumbnail;
            }

            items.Add(new MediaSearchItem(
                "bilibili",
                bvid,
                title.Length > 0 ? title : bvid,
                author,
                $"https://www.bilibili.com/video/{bvid}",
                thumbnail,
                ParseClock(SearchJson.ReadString(entry, "duration")),
                SearchJson.ReadLong(entry, "play")));
        }

        return items;
    }

    private static string CleanTitle(string raw)
    {
        return WebUtility.HtmlDecode(TagRegex.Replace(raw, string.Empty)).Trim();
    }

    /// <summary>把 "5:17" / "1:02:03" 这种时钟格式转成秒</summary>
    private static double ParseClock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        double total = 0;
        foreach (var part in raw.Split(':'))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            total = total * 60 + value;
        }

        return total;
    }
}

internal static class YoutubeSearch
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 用 yt-dlp 的 ytsearchN: 搜视频，-J --flat-playlist 直接拿每条结果的结构化信息。
    /// 只搜不解析：比抓 HTML 稳，也不需要 ffmpeg。
    /// </summary>
    public static async Task<IReadOnlyList<MediaSearchItem>> SearchAsync(
        string keyword,
        int limit,
        string? proxy,
        CancellationToken cancellationToken = default)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return Array.Empty<MediaSearchItem>();
        }

        limit = Math.Clamp(limit, 1, 30);

        var ytDlp = YtDlpLocator.Find()
            ?? throw new InvalidOperationException(
                "找不到 yt-dlp，无法搜索 YouTube。安装方式（任选其一）：\n" +
                "  · python -m pip install --user -U yt-dlp（国内可用 -i https://mirrors.aliyun.com/pypi/simple/）\n" +
                "  · 放一个 yt-dlp.exe 到 PATH，或用环境变量 MUSICPLAYER_YTDLP 指定路径");

        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlp.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // yt-dlp 输出 UTF-8；不显式指定的话中文标题会按控制台代码页解码成乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var argument in ytDlp.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in new[]
        {
            "--ignore-config",
            "-J",
            "--flat-playlist",
            "--no-warnings",
            "--no-playlist",
            "--socket-timeout", "15",
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
        startInfo.ArgumentList.Add($"ytsearch{limit}:{text}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("yt-dlp 启动失败。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            throw new InvalidOperationException(cancellationToken.IsCancellationRequested
                ? "YouTube 搜索已取消。"
                : $"YouTube 搜索超时（{SearchTimeout.TotalSeconds:F0} 秒）。国内网络通常需要代理：" +
                  "启动宿主时加 --proxy http://127.0.0.1:7897，或在系统代理里配好。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(YtDlpAuthentication.DescribeFailure(stderr, process.ExitCode));
        }

        return Parse(stdout, limit);
    }

    /// <summary>把 yt-dlp -J 的 JSON 转成列表（internal 便于用真实响应单独验证解析）</summary>
    internal static IReadOnlyList<MediaSearchItem> Parse(string stdout, int limit)
    {
        var items = new List<MediaSearchItem>();

        // yt-dlp 正常时 stdout 就是一段 JSON，保险起见从第一个 '{' 开始解析
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"yt-dlp 没有返回 JSON：{SearchJson.Shorten(stdout, 300)}");
        }

        using var document = JsonDocument.Parse(stdout[start..]);
        if (!document.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (items.Count >= limit)
            {
                break;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = SearchJson.ReadString(entry, "id");
            if (id.Length == 0)
            {
                continue;
            }

            var title = SearchJson.ReadString(entry, "title");
            var author = SearchJson.ReadString(entry, "channel");
            if (author.Length == 0)
            {
                author = SearchJson.ReadString(entry, "uploader");
            }

            var url = SearchJson.ReadString(entry, "webpage_url");
            if (url.Length == 0)
            {
                url = $"https://www.youtube.com/watch?v={id}";
            }

            items.Add(new MediaSearchItem(
                "youtube",
                id,
                title.Length > 0 ? title : id,
                author,
                url,
                PickThumbnail(entry, id),
                SearchJson.ReadDouble(entry, "duration"),
                SearchJson.ReadLong(entry, "view_count")));
        }

        return items;
    }

    /// <summary>缩略图优先用 yt-dlp 给的，没有就按视频 ID 拼 YouTube 固定的封面地址</summary>
    private static string PickThumbnail(JsonElement entry, string id)
    {
        if (entry.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (var thumbnail in thumbnails.EnumerateArray().Reverse())
            {
                if (thumbnail.ValueKind == JsonValueKind.Object)
                {
                    var url = SearchJson.ReadString(thumbnail, "url");
                    if (url.Length > 0)
                    {
                        return url;
                    }
                }
            }
        }

        return $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已经退出 / 没权限都无所谓
        }
    }
}
