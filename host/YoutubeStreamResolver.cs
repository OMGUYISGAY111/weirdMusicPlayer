using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MusicPlayer.Host;

/// <summary>yt-dlp 仅解析元数据及音轨 URL，不下载媒体或执行音频后处理。</summary>
internal static class YoutubeResolver
{
    public static async Task<RemoteAudioTrack> ResolveAsync(string input, string? proxy,
        CancellationToken cancellationToken = default, bool preferOpus = true)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("请填 YouTube 视频地址。");
        var command = YtDlpLocator.Find() ?? throw new InvalidOperationException("找不到 yt-dlp，请安装或配置 MUSICPLAYER_YTDLP。");
        var start = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in command.PrefixArguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--ignore-config", "--no-cache-dir", "--no-playlist", "--no-progress", "--skip-download", "--dump-single-json", "--socket-timeout", "20", "-f",
            preferOpus ? "bestaudio[acodec=opus][ext=webm][protocol=https]/" + YtDlpRuntime.AudioFormat : YtDlpRuntime.AudioFormat })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--proxy"); start.ArgumentList.Add(proxy ?? "");
        YtDlpAuthentication.Apply(start);
        YtDlpRuntime.Apply(start);
        start.ArgumentList.Add(input.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("yt-dlp 启动失败。");
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var json = await output;
        var stderr = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException(YtDlpAuthentication.DescribeFailure(stderr, process.ExitCode));
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement, proxy) with { SourceKind = "youtube", OriginalUrl = input };
    }

    internal static RemoteAudioTrack Parse(JsonElement data, string? proxy)
    {
        string Text(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var url = Text("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("YouTube 未返回可用的音频流地址。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("http_headers", out var rawHeaders) && rawHeaders.ValueKind == JsonValueKind.Object)
            foreach (var header in rawHeaders.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String && !header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    headers[header.Name] = header.Value.GetString()!;
        var mime = (Text("ext"), Text("acodec"), Text("vcodec")) switch
        {
            ("webm", "opus", "none") => "audio/webm; codecs=\"opus\"",
            ("webm", "vorbis", "none") => "audio/webm; codecs=\"vorbis\"",
            ("m4a", "mp4a.40.2", _) => "audio/mp4; codecs=\"mp4a.40.2\"",
            ("mp3", _, _) => "audio/mpeg",
            _ => "",
        };
        // 清单不是普通媒体文件，交给 FFmpeg 处理分片。
        if (Text("protocol").Contains("m3u8") || Text("protocol").Contains("dash_segments")) mime = "";
        var duration = data.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : 0;
        return new RemoteAudioTrack(Text("id"), Text("title"), Text("uploader"), duration, url, mime, headers, proxy)
            { Cookies = ParseCookies(Text("cookies"), uri) };
    }

    // yt-dlp 把 Cookie 从 http_headers 移到带 Domain/Path 属性的独立字段。
    // 只接受有作用域的条目，HTTP 重定向时不能把 Cookie 原样转发给其他域名。
    internal static System.Net.CookieContainer ParseCookies(string raw, Uri uri)
    {
        var jar = new System.Net.CookieContainer();
        System.Net.Cookie? cookie = null;
        void Add()
        {
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Domain)) return;
            try { jar.Add(uri, cookie); } catch (System.Net.CookieException) { }
        }
        foreach (var part in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? "" : part[(separator + 1)..];
            switch (name.ToLowerInvariant())
            {
                case "domain": if (cookie != null) cookie.Domain = value; break;
                case "path": if (cookie != null) cookie.Path = value; break;
                case "secure": if (cookie != null) cookie.Secure = true; break;
                case "expires":
                    if (cookie != null && long.TryParse(value, out var seconds))
                        try { cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
                    break;
                case "version": break;
                default:
                    Add(); cookie = null;
                    try { if (separator > 0) cookie = new System.Net.Cookie(name, value, "/"); } catch (System.Net.CookieException) { }
                    break;
            }
        }
        Add();
        return jar;
    }
}
