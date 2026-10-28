using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace MusicPlayer.Host;

/// <summary>有界异步 JSONL 日志；日志不可写或队列满时不能影响播放线程。</summary>
internal static class PlaybackLog
{
    private sealed record Item(string? Line, TaskCompletionSource? Barrier = null);
    private static Channel<Item>? _queue;
    private static Task? _writer;
    private static Mutex? _mutex;
    private static long _limit;
    private static int _backups;
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public static string DirectoryPath { get; private set; } = "";

    public static bool Initialize(string userDataDirectory, long maxBytes = 2 * 1024 * 1024, int backups = 4)
    {
        if (_queue != null) return true;
        try
        {
            DirectoryPath = Path.Combine(Path.GetFullPath(userDataDirectory), "logs");
            Directory.CreateDirectory(DirectoryPath);
            _limit = Math.Max(512, maxBytes); _backups = Math.Clamp(backups, 1, 10);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())))[..20];
            _mutex = new Mutex(false, "Local\\MusicPlayerLog-" + key);
            var queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            _queue = queue;
            _writer = Task.Run(async () =>
            {
                await foreach (var item in queue.Reader.ReadAllAsync())
                {
                    if (item.Barrier != null) { item.Barrier.TrySetResult(); continue; }
                    try { Append(item.Line!); } catch { /* 日志不可写不能导致播放失败 */ }
                }
            });
            return true;
        }
        catch { return false; }
    }

    public static void Write(string component, string eventName, object? details = null, string level = "info")
    {
        if (_queue == null) return;
        try
        {
            var data = JsonSerializer.SerializeToElement(details);
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow.ToString("O"), session = Session, pid = Environment.ProcessId,
                level = level is "warn" or "error" ? level : "info",
                component = Name(component), @event = Name(eventName), data = Clean(data, 0),
            }, JsonOptions);
            _queue.Writer.TryWrite(new Item(line)); // 队列满时丢弃诊断，绝不阻塞音频。
        }
        catch { }
    }

    public static void Exception(string component, string eventName, Exception error, object? context = null) =>
        Write(component, eventName, new { errorType = error.GetType().Name, message = error.Message, stack = error.StackTrace, context }, "error");

    public static async Task FlushAsync()
    {
        if (_queue == null) return;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _queue.Writer.WriteAsync(new Item(null, barrier)); await barrier.Task; } catch { }
    }

    public static void Shutdown()
    {
        _queue?.Writer.TryComplete();
        try { _writer?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    private static void Append(string line)
    {
        var acquired = false;
        try
        {
            try { acquired = _mutex!.WaitOne(1000); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            var file = Path.Combine(DirectoryPath, "playback.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) + 1 > _limit)
            {
                for (var i = _backups; i >= 1; i--)
                {
                    var destination = Path.Combine(DirectoryPath, $"playback.{i}.jsonl");
                    var source = i == 1 ? file : Path.Combine(DirectoryPath, $"playback.{i - 1}.jsonl");
                    if (File.Exists(destination)) File.Delete(destination);
                    if (File.Exists(source)) File.Move(source, destination);
                }
            }
            File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
        }
        finally { if (acquired) _mutex!.ReleaseMutex(); }
    }

    private static string Name(string text) => Regex.IsMatch(text, "^[a-zA-Z0-9_.-]{1,64}$") ? text : "unknown";
    private static object? Clean(JsonElement value, int depth)
    {
        if (depth > 5) return "[truncated]";
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            foreach (var property in value.EnumerateObject().Take(40))
            {
                var key = property.Name.Length > 64 ? property.Name[..64] : property.Name;
                result[key] = Regex.IsMatch(key, "cookie|authorization|token|secret|password|credential|headers|sessdata|access.?key|api.?key|signature|^sign$|^session$", RegexOptions.IgnoreCase)
                    ? "[redacted]" : Clean(property.Value, depth + 1);
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Take(24).Select(item => Clean(item, depth + 1)).ToArray();
        return value.ValueKind switch
        {
            JsonValueKind.String => Redact(value.GetString() ?? ""),
            JsonValueKind.Number => value.Clone(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null,
        };
    }

    internal static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:https?|socks5h?|blob|file)://[^\s\""'<>]+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? $"{uri.Scheme}://{uri.Host}/[redacted]" : "[local-source]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?i)(cookie|authorization|token|secret|password|signature|sign|sessdata|access_key|api_key)\s*[\""']?\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^,\r\n}]+)", "$1=[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,\""']+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?:[A-Za-z]:\\|\\\\)[^\r\n\""<>|]+", "[local-path]");
        return text.Length > 2000 ? text[..2000] + "[truncated]" : text;
    }
}
