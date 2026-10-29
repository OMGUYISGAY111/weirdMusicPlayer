using System.Text.Json;
using MusicPlayer.Host;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// 搜索和解析共用此工厂，防止系统代理再次混入 B站 API / CDN 请求。
foreach (var useCookies in new[] { true, false })
{
    using var handler = BilibiliHttp.CreateHandler(useCookies);
    Check(!handler.UseProxy && handler.UseCookies == useCookies, "B站请求必须直连并保留各自的 Cookie 策略");
}

using var biliJson = JsonDocument.Parse("""
{"data":{"result":[null,{},
  {"bvid":"BV1GJ411x7h7","title":"<em>晴天</em> &amp; MV","author":"歌手","pic":"//i.example/cover.jpg","duration":"1:02:03","play":"12000"},
  {"bvid":"BV1234567890","title":"第二条"}]}}
""");
var bili = BilibiliSearch.Parse(biliJson.RootElement, 1);
Check(bili.Count == 1 && bili[0].Title == "晴天 & MV", "B站标题/数量解析失败");
Check(bili[0].Duration == 3723 && bili[0].PlayCount == 12000, "B站时长/播放量解析失败");
Check(bili[0].Url == "https://www.bilibili.com/video/BV1GJ411x7h7", "B站视频链接错误");
Check(bili[0].Thumbnail == "https://i.example/cover.jpg", "B站封面协议错误");
var youtube = YoutubeSearch.Parse("""
{"entries":[null,{}, {"id":"dQw4w9WgXcQ","title":"晴天","uploader":"频道","duration":null,"view_count":null}]}
""", 6);
Check(youtube.Count == 1 && youtube[0].Author == "频道", "YouTube缺字段兼容失败");
Check(youtube[0].Url == "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "YouTube视频链接错误");
Console.WriteLine("PASS: B站 / YouTube 响应解析和视频链接检查");

using (var ytInfo = JsonDocument.Parse("""
{"id":"dQw4w9WgXcQ","title":"YouTube 音乐视频","channel":"频道", "uploader":"上传者",
 "thumbnail":"http://i.ytimg.com/cover.jpg","duration":210,"view_count":123456}
"""))
{
    var info = YoutubeSearch.ParseVideoInfo(ytInfo.RootElement);
    Check(info.Title == "YouTube 音乐视频" && info.Author == "频道", "YouTube 标题和频道解析失败");
    Check(info.Thumbnail == "https://i.ytimg.com/cover.jpg" && info.Duration == 210 && info.PlayCount == 123456, "YouTube 视频详细信息解析失败");
}
using (var ytMinimal = JsonDocument.Parse("""{"id":"dQw4w9WgXcQ","uploader":"上传者","duration":null,"thumbnails":[{}, {"url":"//i.ytimg.com/cover.jpg"}]}"""))
{
    var info = YoutubeSearch.ParseVideoInfo(ytMinimal.RootElement);
    Check(info.Title == "dQw4w9WgXcQ" && info.Author == "上传者" && info.Duration == 0, "YouTube 缺字段容错失败");
    Check(info.Thumbnail == "https://i.ytimg.com/cover.jpg", "YouTube 封面数组解析失败");
}
var metadataArgs = YoutubeResolver.VideoArguments(null);
Check(metadataArgs.Contains("--skip-download") && metadataArgs.Contains("--no-playlist")
    && metadataArgs.Contains("--ignore-no-formats-error") && !metadataArgs.Contains("-f"), "读取信息不能下载视频或要求存在音轨格式");
Check(YoutubeResolver.VideoArguments(false).Contains(YtDlpRuntime.AudioFormat)
    && YoutubeResolver.VideoArguments(true).Any(value => value.StartsWith("bestaudio[acodec=opus]")), "播放格式偏好必须保留");
Console.WriteLine("PASS: YouTube 视频信息、封面、频道及只读参数检查");

if (args.Length > 1 && args[0] == "--info-youtube")
{
    using var infoTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        var info = await YoutubeResolver.GetVideoInfoAsync(args[1],
            Environment.GetEnvironmentVariable("MUSICPLAYER_PROXY") ?? SystemProxy.Detect(), infoTimeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(info));
        Check(!string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(info.Thumbnail), "YouTube 真实视频标题或封面缺失");
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

using (var videoInfo = JsonDocument.Parse("""
{"bvid":"BV1GJ411x7h7","title":"视频原始标题 & 音乐","pic":"//i.example/cover.jpg",
 "owner":{"name":"UP 主"},"duration":123,"stat":{"view":456}}
"""))
{
    var info = BilibiliResolver.ParseVideoInfo(videoInfo.RootElement, "https://www.bilibili.com/video/BV1GJ411x7h7/?p=2");
    Check(info.Title == "视频原始标题 & 音乐" && info.Author == "UP 主", "视频标题和作者读取失败");
    Check(info.Thumbnail == "https://i.example/cover.jpg" && info.Duration == 123 && info.PlayCount == 456, "封面、时长和播放量读取失败");
    Check(info.Url.EndsWith("?p=2"), "视频信息不应丢失原链接参数");
}
using (var missingInfo = JsonDocument.Parse("""{"owner":null,"stat":null,"pic":null}"""))
{
    var info = BilibiliResolver.ParseVideoInfo(missingInfo.RootElement, "https://www.bilibili.com/video/BV1GJ411x7h7");
    Check(info.Title == "BV1GJ411x7h7" && info.Thumbnail == "" && info.Author == "", "信息缺失时应保留 BV 号并容错");
}
Console.WriteLine("PASS: B站视频信息标题、封面、作者与缺字段容错检查");
using (var httpCover = JsonDocument.Parse("""{"pic":"http://i.example/cover.jpg"}"""))
    Check(BilibiliResolver.ParseVideoInfo(httpCover.RootElement, "https://www.bilibili.com/video/BV1GJ411x7h7").Thumbnail
        == "https://i.example/cover.jpg", "封面应升级为 HTTPS，避免安全页面拦截混合内容");

if (args.Length > 1 && args[0] == "--info-bili")
{
    using var infoTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var info = await BilibiliResolver.GetVideoInfoAsync(args[1], infoTimeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(info));
        Check(!string.IsNullOrWhiteSpace(info.Title) && !string.IsNullOrWhiteSpace(info.Thumbnail), "真实视频标题或封面缺失");
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

Check(YtDlpAuthentication.BuildArguments(null, null).Count == 0, "未配置时不应读取浏览器 Cookie");
Check(YtDlpAuthentication.BuildArguments(null, " firefox:default ").SequenceEqual(
    new[] { "--cookies-from-browser", "firefox:default" }), "浏览器配置没有原样传递");
var cookieTestDir = Path.Combine(Path.GetTempPath(), "MusicPlayerCookieConfigCheck", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cookieTestDir);
var cookieTestFile = Path.Combine(cookieTestDir, "测试 cookies.txt");
try
{
    File.WriteAllText(cookieTestFile, "# Netscape HTTP Cookie File\n"); // 空测试夹具，不使用真实登录信息。
    Check(YtDlpAuthentication.BuildArguments(cookieTestFile, "edge").SequenceEqual(
        new[] { "--cookies", cookieTestFile }), "Cookie 文件应优先于浏览器且路径保留为单个参数");
    var missingRejected = false;
    try { YtDlpAuthentication.BuildArguments(cookieTestFile + ".missing", null); }
    catch (InvalidOperationException) { missingRejected = true; }
    Check(missingRejected, "不存在的 Cookie 文件应给出明确错误");
}
finally { File.Delete(cookieTestFile); Directory.Delete(cookieTestDir); }
Check(YtDlpAuthentication.DescribeFailure("Sign in to confirm you’re not a bot", 1)
    .Contains(YtDlpAuthentication.CookieFileVariable), "人机验证错误缺少配置提示");
Check(YtDlpAuthentication.DescribeFailure("ERROR: Failed to decrypt cookies", 1)
    .Contains("解密"), "Cookie 解密错误没有解释");
Check(YtDlpAuthentication.DescribeFailure("ERROR: video unavailable", 1)
    .Contains("video unavailable"), "其他错误应保留诊断信息");
Console.WriteLine("PASS: YouTube Cookie 配置、参数边界和错误提示检查（无真实 Cookie）");

Check(YtDlpRuntime.BuildArguments(null).Count == 0, "没有 Node 时不应覆盖默认运行时");
Check(YtDlpRuntime.BuildArguments(@"C:\含空格路径\node.exe").SequenceEqual(
    new[] { "--js-runtimes", @"node:C:\含空格路径\node.exe" }), "Node 程序路径应保留为单个参数");
Check(YtDlpAuthentication.DescribeFailure("WARNING: Signature solving failed\nRequested format is not available", 1)
    .Contains("签名解析"), "签名错误不应被误报为普通格式选择失败");
Check(YtDlpAuthentication.DescribeFailure("ERROR: Requested format is not available", 1)
    .Contains("未返回可用音频"), "格式缺失应给出明确解释");
Console.WriteLine("PASS: JavaScript 运行时配置及格式失败诊断");

using (var streamJson = JsonDocument.Parse("""
{"id":"track","title":"音频","uploader":"作者","duration":12.5,
 "url":"https://cdn.example/audio?expire=123","ext":"webm","acodec":"opus","vcodec":"none","protocol":"https",
 "http_headers":{"User-Agent":"test-agent","Referer":"https://www.youtube.com/"}}
"""))
{
    var track = YoutubeResolver.Parse(streamJson.RootElement, "http://127.0.0.1:7897");
    Check(track.MimeType == "audio/webm; codecs=\"opus\"", "Opus 容器和编码识别错误");
    Check(track.Headers["Referer"] == "https://www.youtube.com/" && track.Proxy == "http://127.0.0.1:7897", "播放时应保留解析的请求头及代理出口");
    Check(track.Duration == 12.5 && track.Id == "track", "流元数据错误");
}
using (var manifest = JsonDocument.Parse("""
{"id":"track","duration":20,"url":"https://cdn.example/audio.m3u8","ext":"m4a","acodec":"mp4a.40.2","protocol":"m3u8_native"}
"""))
    Check(YoutubeResolver.Parse(manifest.RootElement, null).MimeType == "", "分片清单不应作为普通音频直链播放");
using (var missing = JsonDocument.Parse("""{"duration":null}"""))
{
    var rejected = false;
    try { YoutubeResolver.Parse(missing.RootElement, null); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "没有音轨 URL 应返回明确错误");
}
Console.WriteLine("PASS: 远端音轨元数据、编码、清单和请求头解析");

var cookieScope = YoutubeResolver.ParseCookies("session=fixture; Domain=.example.com; Path=/audio; Secure; Expires=4102444800; other=ignored; Domain=.other.test; Path=/; unscoped=ignored",
    new Uri("https://cdn.example.com/audio/track"));
Check(cookieScope.GetCookieHeader(new Uri("https://cdn.example.com/audio/track")) == "session=fixture", "只应使用匹配域名、路径和协议的 Cookie");
Check(cookieScope.GetCookieHeader(new Uri("https://unrelated.test/audio/track")) == "", "Cookie 不应泄漏到重定向的其他域名");
Check(cookieScope.GetCookieHeader(new Uri("http://cdn.example.com/audio/track")) == "", "Secure Cookie 不应发给 HTTP");
Console.WriteLine("PASS: yt-dlp Cookie 作用域保留（仅合成夹具，无真实账号）");

if (args.Length > 1 && args[0] == "--resolve-youtube")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        var track = await YoutubeResolver.ResolveAsync(args[1],
            Environment.GetEnvironmentVariable("MUSICPLAYER_PROXY") ?? SystemProxy.Detect(), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { track.Id, track.Title, track.Duration, track.MimeType }));
        Check(Uri.TryCreate(track.Url, UriKind.Absolute, out _), "YouTube 音频地址缺失");
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

if (args.Length > 1 && args[0] == "--resolve-bili")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        var track = await BilibiliResolver.ResolveAsync(args[1], timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { track.Id, track.Title, track.Duration, track.MimeType }));
        Check(Uri.TryCreate(track.Url, UriKind.Absolute, out _), "音频地址缺失");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
}

if (args.Contains("--live"))
{
    var keyword = args.Length > 1 ? args[1] : "晴天 周杰伦 叶惠美";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
    async Task Probe(string source, Func<Task<IReadOnlyList<MediaSearchItem>>> search)
    {
        try
        {
            var items = await search();
            Console.WriteLine(JsonSerializer.Serialize(new { source, keyword, items }));
            if (items.Count == 0) Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{source}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
    await Task.WhenAll(
        Probe("bilibili", () => BilibiliSearch.SearchAsync(keyword, 3, timeout.Token)),
        Probe("youtube", () => YoutubeSearch.SearchAsync(keyword, 3, SystemProxy.Detect(), timeout.Token)));
}
