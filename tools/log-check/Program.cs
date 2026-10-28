using MusicPlayer.Host;
using System.Text.Json;

static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
var root = Path.Combine(Path.GetTempPath(), "MusicPlayerLogChecks", Guid.NewGuid().ToString("N"));
Check(PlaybackLog.Initialize(root, 4096, 2), "日志初始化失败");
PlaybackLog.Write("test", "redaction", new
{
    source = "stream://media/ab", trackId = "BV1cdXtY9EX8", cookie = "FAKE_COOKIE_SECRET",
    access_key = "FAKE_ACCESS_SECRET", apiKey = "FAKE_API_SECRET", signature = "FAKE_SIGNATURE_SECRET",
    nested = new { Authorization = "Bearer FAKE_AUTH_SECRET", headers = new { Cookie = "FAKE_NESTED_SECRET" } },
    message = "request https://test:FAKE_PASSWORD@cdn.example/audio/private?token=FAKE_SIGN_SECRET\nCookie: SID=FAKE_HEADER_SECRET\nAuthorization: Bearer FAKE_BEARER_SECRET\nSESSDATA=FAKE_SESSION_SECRET",
    path = @"C:\Users\FAKE_PERSON\private\cookies.txt", position = 37.5,
});
await PlaybackLog.FlushAsync();
var log = Path.Combine(PlaybackLog.DirectoryPath, "playback.jsonl");
var first = File.ReadAllText(log);
Check(!first.Contains("FAKE_"), "敏感测试数据未完全隐藏");
using (var doc = JsonDocument.Parse(first))
{
    Check(doc.RootElement.GetProperty("data").GetProperty("source").GetString() == "stream://media/ab", "应保留流 ID 关联记录");
    Check(doc.RootElement.GetProperty("data").GetProperty("trackId").GetString() == "BV1cdXtY9EX8", "应保留公共视频 ID");
    Check(DateTimeOffset.TryParse(doc.RootElement.GetProperty("time").GetString(), out _), "日志时间缺失");
}
Console.WriteLine("PASS: URL 签名、Cookie、Authorization、嵌套凭据及本地路径脱敏，流 ID 保留");

Parallel.For(0, 400, index => PlaybackLog.Write("test", "rotation", new { index, text = new string('x', 150) }));
await PlaybackLog.FlushAsync();
var files = Directory.GetFiles(PlaybackLog.DirectoryPath, "playback*.jsonl");
Check(files.Length == 3, "轮转文件数量不正确");
foreach (var file in files)
{
    Check(new FileInfo(file).Length <= 4096, "日志超过文件大小限制");
    foreach (var line in File.ReadLines(file)) using (JsonDocument.Parse(line)) { }
}
Console.WriteLine("PASS: 并发写入、逐行 JSON 有效、大小轮转和历史数量上限");

// 用测试目录内的同名文件暂时模拟日志路径不可写，不触碰用户数据。
foreach (var file in files) File.Delete(file);
Directory.Delete(PlaybackLog.DirectoryPath);
File.WriteAllText(PlaybackLog.DirectoryPath, "blocked");
PlaybackLog.Write("test", "disk_unavailable");
await PlaybackLog.FlushAsync();
File.Delete(PlaybackLog.DirectoryPath);
Directory.CreateDirectory(PlaybackLog.DirectoryPath);
PlaybackLog.Write("test", "disk_restored");
await PlaybackLog.FlushAsync();
Check(File.ReadAllText(log).Contains("disk_restored"), "磁盘路径恢复后应可继续记录");
PlaybackLog.Shutdown();
Console.WriteLine("PASS: 日志不可写不抛错，恢复后继续写入，刷新和关闭完成");
