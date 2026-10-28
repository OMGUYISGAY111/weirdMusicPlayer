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

if (args.Length > 1 && args[0] == "--resolve-youtube")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        var track = await YoutubeResolver.ResolveAsync(args[1],
            Path.Combine(Path.GetTempPath(), "MusicPlayerYoutubeProbe"),
            Environment.GetEnvironmentVariable("MUSICPLAYER_PROXY") ?? SystemProxy.Detect(), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(track));
        Check(File.Exists(track.Mp3Path) && new FileInfo(track.Mp3Path).Length > 1024, "YouTube 音频缓存缺失");
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; }
}

if (args.Length > 1 && args[0] == "--resolve-bili")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var cache = Path.Combine(Path.GetTempPath(), "MusicPlayerBiliProbe");
    try
    {
        var track = await BilibiliResolver.ResolveAsync(args[1], cache, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(track));
        Check(File.Exists(track.Mp3Path) && new FileInfo(track.Mp3Path).Length > 1024, "音频缓存缺失");
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
