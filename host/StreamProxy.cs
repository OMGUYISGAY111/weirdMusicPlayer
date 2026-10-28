using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Callback;

namespace MusicPlayer.Host;

/// <summary>
/// stream:// 的自定义 scheme 代理。
///
/// 为什么需要它：prod 下页面是 `app://web`（secure origin），直接加载 `http://` 媒体会被
/// Chromium 当混合内容处理（请求被自动升级成 https 后失败）。让**宿主进程**去取内容，
/// 再通过我们自己的 secure scheme 喂给页面，就绕开了这个限制；
/// 顺带还能统一注入 Referer / Authorization / Cookie 等请求头（后续接 B站/YouTube 用）。
///
/// 用法：
///   1. 页面调用 hostApi.registerStream(url, headersJson) -> 返回 "stream://media/{id}"
///   2. 把返回的地址当普通 src 用（支持 Range、seek、buffered）
/// </summary>
internal sealed record StreamDescriptor(string Url, Dictionary<string, string> Headers, string? LocalPath,
    RemoteAudioTrack? Track = null, bool Transcode = false);

internal static class StreamRegistry
{
    private static readonly ConcurrentDictionary<string, StreamDescriptor> Items = new();
    private static readonly ConcurrentDictionary<string, bool> Failed = new();
    private static int _sequence;

    /// <summary>注册一个上游地址，返回页面可用的 stream:// 地址</summary>
    public static string Register(string url, string? headersJson = null)
    {
        var headers = ParseHeaders(headersJson);
        return Add(new StreamDescriptor(url, headers, null));
    }

    /// <summary>注册用户本地文件，支持 Range/seek；在线音轨不使用此路径。</summary>
    public static string RegisterLocalFile(string path)
    {
        var full = Path.GetFullPath(path);
        return Add(new StreamDescriptor(string.Empty, new Dictionary<string, string>(), full));
    }

    public static string RegisterRemote(RemoteAudioTrack track) => Add(new StreamDescriptor(track.Url, track.Headers, null, track));
    public static string RegisterTranscoded(RemoteAudioTrack track) => Add(new StreamDescriptor(track.Url, track.Headers, null, track, true));

    private static string Add(StreamDescriptor descriptor)
    {
        // 注册项只持有元数据；限制长期使用时失效 URL 的内存占用。
        if (Items.Count >= 256)
            foreach (var key in Items.Keys.OrderBy(key => Convert.ToInt32(key, 16)).Take(64))
            {
                Items.TryRemove(key, out _);
                Failed.TryRemove(key, out _);
            }
        var id = Interlocked.Increment(ref _sequence).ToString("x");
        Items[id] = descriptor;
        PlaybackLog.Write("stream", "registered", new { source = $"stream://media/{id}",
            mode = descriptor.Transcode ? "transcode" : descriptor.LocalPath != null ? "local" : "direct",
            platform = descriptor.Track?.SourceKind, trackId = descriptor.Track?.Id,
            duration = descriptor.Track?.Duration, mime = descriptor.Track?.MimeType });
        return $"stream://media/{id}";
    }

    public static bool TryGet(string id, out StreamDescriptor descriptor)
    {
        if (Items.TryGetValue(id, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = new StreamDescriptor(string.Empty, new Dictionary<string, string>(), null);
        return false;
    }

    public static void MarkFailed(string id, bool recoverable = true)
    {
        if (Failed.TryAdd(id, recoverable)) PlaybackLog.Write("stream", "failed", new { source = $"stream://media/{id}", recoverable }, "error");
    }
    public static bool HasFailed(string id) => Failed.ContainsKey(id);
    public static string GetError(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "stream" || uri.Host != "media") return "";
        return HasFailed(uri.AbsolutePath.Trim('/')) ? "音频流加载或解码失败，请点击重试重新获取音轨。" : "";
    }

    public static bool IsRecoverable(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Failed.TryGetValue(uri.AbsolutePath.Trim('/'), out var recoverable) && recoverable;

    public static bool CanRecover(Exception error) => error switch
    {
        AudioStreamException audio => audio.Recoverable,
        TimeoutException or HttpRequestException or OperationCanceledException => true,
        FileNotFoundException or InvalidOperationException => false,
        _ => error is IOException,
    };

    private static Dictionary<string, string> ParseHeaders(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (parsed != null)
            {
                foreach (var pair in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        result[pair.Key] = pair.Value ?? string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"stream:// headers 解析失败：{ex.Message}");
        }

        return result;
    }
}

/// <summary>宿主进程内共享的 HttpClient（大文件用 ResponseHeadersRead 流式读取）</summary>
internal static class ProxyHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(120),
    };
}

internal sealed class StreamProxySchemeHandlerFactory : ISchemeHandlerFactory
{
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            return new ResourceHandler();
        }

        var id = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(id) ? new ResourceHandler() : new StreamProxyHandler(id);
    }
}

/// <summary>
/// 把上游响应原样转给 CEF：保留状态码（200/206）、Content-Length、Content-Type、
/// Content-Range，并按需分块读取，内存占用与文件大小无关。
/// </summary>
internal sealed class StreamProxyHandler : IResourceHandler
{
    private readonly string _id;

    private HttpResponseMessage? _response;
    private HttpClient? _client;
    private readonly CancellationTokenSource _cancel = new();
    private Stream? _body;
    private int _status = 200;
    private long _length = -1;
    /// <summary>本次响应还能读多少字节（-1 表示未知）</summary>
    private long _remaining = -1;
    private string? _contentRange;
    private string _mimeType = "application/octet-stream";
    private bool _headOnly;
    private long _bytesRead;
    // Open 已通过本地 Seek 或上游 206 响应跳过的字节。
    // CEF 仍会为 Range 调用 Skip；先确认这些字节，不能再次推进流位置。
    private long _preSkippedBytes;

    /// <summary>只在出错时记一行日志（成功路径每请求一次，写文件太吵）</summary>
    private static void Log(string message)
    {
        try
        {
            Trace.WriteLine($"stream:// {message}");
            PlaybackLog.Write("stream", "request_error", new { message }, "error");
        }
        catch
        {
            // 忽略
        }
    }

    public StreamProxyHandler(string id)
    {
        _id = id;
    }

    /* ---------- CEF 资源处理器契约 ---------- */

    public bool Open(IRequest request, out bool handleRequest, ICallback callback)
    {
        // 同步取响应头（ResponseHeadersRead：只等头部，不缓冲整个文件）。
        // 阻塞时间≈一次上游握手，换来实现简单可靠、各 CefSharp 版本行为一致。
        handleRequest = true;
        callback.Dispose();

        try
        {
            if (!StreamRegistry.TryGet(_id, out var descriptor))
            {
                Log($"未注册的 id：{_id}");
                return false;
            }

            // CEF 会自动续传断开的媒体请求。已确认失败的流必须返回失败状态，
            // 否则浏览器可能无限重试、始终不触发媒体 error，界面还停在播放状态。
            if (StreamRegistry.HasFailed(_id))
            {
                _status = 502;
                _length = _remaining = 0;
                // CEF 在读取响应头前可能先确认 Range 偏移，确认后才会看到 502。
                var failedRange = Regex.Match(request.Headers?["Range"] ?? "", "^bytes=(\\d+)-");
                if (failedRange.Success && long.TryParse(failedRange.Groups[1].Value, out var failedStart))
                    _preSkippedBytes = failedStart;
                return true;
            }

            _headOnly = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
            var range = request.Headers?["Range"];
            Trace.WriteLine($"stream:// {_id} open {request.Method} range={range ?? "none"}");
            PlaybackLog.Write("stream", "request_open", new { source = $"stream://media/{_id}", method = request.Method, range });

            if (!string.IsNullOrEmpty(descriptor.LocalPath))
            {
                return OpenLocalFile(descriptor.LocalPath!, range);
            }

            if (descriptor.Transcode && descriptor.Track != null)
            {
                var ffmpeg = FfmpegLocator.Find() ?? throw new InvalidOperationException("找不到 ffmpeg。");
                return OpenSeekable(new VirtualWaveStream(descriptor.Track, ffmpeg), "audio/wav", range);
            }

            var method = request.Method ?? "GET";
            using var message = new HttpRequestMessage(new HttpMethod(method), descriptor.Url);
            foreach (var header in descriptor.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (!string.IsNullOrEmpty(range))
            {
                message.Headers.TryAddWithoutValidation("Range", range);
            }

            var client = ProxyHttp.Client;
            if (descriptor.Track != null)
            {
                var handler = new HttpClientHandler { UseProxy = !string.IsNullOrWhiteSpace(descriptor.Track.Proxy),
                    Proxy = string.IsNullOrWhiteSpace(descriptor.Track.Proxy) ? null : new System.Net.WebProxy(descriptor.Track.Proxy),
                    UseCookies = true, CookieContainer = descriptor.Track.Cookies, AutomaticDecompression = System.Net.DecompressionMethods.None };
                client = _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            }
            var response = client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _cancel.Token)
                .GetAwaiter()
                .GetResult();

            _response = response;
            _status = (int)response.StatusCode;
            if (_status >= 400) StreamRegistry.MarkFailed(_id, _status is 401 or 403 or 408 or 429 || _status >= 500);
            _length = response.Content.Headers.ContentLength ?? -1;
            _remaining = _length;
            _contentRange = response.Content.Headers.ContentRange?.ToString();
            _preSkippedBytes = response.Content.Headers.ContentRange?.From ?? 0;
            _mimeType = descriptor.Track?.MimeType.Split(';')[0] ?? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            if (!_headOnly)
            {
                _body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"上游失败 {_id}: {ex.GetType().Name}");
            if (!_cancel.IsCancellationRequested) StreamRegistry.MarkFailed(_id, StreamRegistry.CanRecover(ex));
            Dispose();
            return false;
        }
    }

    /// <summary>本地文件：自己算 200/206 和 Content-Range，然后从对应偏移读</summary>
    private bool OpenLocalFile(string path, string? range)
    {
        if (!File.Exists(path))
        {
            Log($"本地文件不存在：{path}");
            return false;
        }

        return OpenSeekable(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "application/octet-stream", range);
    }

    private bool OpenSeekable(Stream stream, string mimeType, string? range)
    {
        _body = stream;
        var total = stream.Length;
        var start = 0L;
        var end = total - 1;

        if (!string.IsNullOrEmpty(range))
        {
            var match = Regex.Match(range, "^bytes=(\\d*)-(\\d*)$");
            if (!match.Success || (!match.Groups[1].Success && !match.Groups[2].Success)
                || (match.Groups[1].Value.Length == 0 && match.Groups[2].Value.Length == 0))
            {
                start = total;
            }
            else
            {
                if (match.Groups[1].Value.Length > 0)
                {
                    if (!long.TryParse(match.Groups[1].Value, out start)) start = total;
                }
                if (match.Groups[2].Value.Length > 0)
                {
                    if (!long.TryParse(match.Groups[2].Value, out var requested)) start = total;
                    else if (match.Groups[1].Value.Length == 0) { start = requested > 0 ? Math.Max(0, total - requested) : total; }
                    else end = Math.Min(requested, total - 1);
                }
            }
        }

        if (start > end || start >= total)
        {
            _status = 416;
            _length = 0;
            _remaining = 0;
            _contentRange = $"bytes */{total}";
            return true;
        }

        _status = string.IsNullOrEmpty(range) ? 200 : 206;
        _length = end - start + 1;
        _remaining = _length;
        _contentRange = string.IsNullOrEmpty(range) ? null : $"bytes {start}-{end}/{total}";
        _mimeType = mimeType;

        if (_headOnly)
        {
            return true;
        }

        stream.Seek(start, SeekOrigin.Begin);
        _preSkippedBytes = start;
        _body = stream;
        return true;
    }

    public bool ProcessRequest(IRequest request, ICallback callback)
    {
        callback.Dispose();
        // 走同步的 Open 路径，这里不再处理
        return false;
    }

    /// <summary>把上游流的一段拷进 dataOut；返回 false 表示响应结束</summary>
    private bool CopyChunk(Stream dataOut, out int bytesRead)
    {
        if (_body == null || _remaining == 0)
        {
            bytesRead = 0;
            return false;
        }

        var want = (int)Math.Min(dataOut.Length, _remaining > 0 ? _remaining : dataOut.Length);
        var buffer = new byte[want];
        try
        {
            var read = _body.ReadAsync(buffer.AsMemory(), _cancel.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(35), _cancel.Token).GetAwaiter().GetResult();
            if (read <= 0)
            {
                if (_remaining > 0) throw new IOException("音频流在预期长度前中断。");
                bytesRead = 0;
                return false;
            }

            dataOut.Write(buffer, 0, read);
            bytesRead = read;
            _bytesRead += read;
            if (_remaining > 0)
            {
                _remaining -= read;
            }

            return true;
        }
        catch (Exception) when (_cancel.IsCancellationRequested)
        {
            bytesRead = -2;
            return false;
        }
        catch (Exception ex)
        {
            Log($"读取中断 {_id}: {ex.Message}");
            StreamRegistry.MarkFailed(_id, StreamRegistry.CanRecover(ex));
            Dispose();
            bytesRead = -2;
            return false;
        }
    }

    public void GetResponseHeaders(IResponse response, out long contentLength, out string redirectUrl)
    {
        response.StatusCode = _status;
        response.StatusText = _status switch
        {
            200 => "OK",
            206 => "Partial Content",
            416 => "Range Not Satisfiable",
            502 => "Bad Gateway",
            _ => string.Empty,
        };
        response.MimeType = _mimeType;
        response.SetHeaderByName("Accept-Ranges", "bytes", true);
        response.SetHeaderByName("Cache-Control", "no-store", true);

        if (!string.IsNullOrEmpty(_contentRange))
        {
            response.SetHeaderByName("Content-Range", _contentRange, true);
        }

        contentLength = _length;
        Trace.WriteLine($"stream:// {_id} headers status={_status} length={_length} range={_contentRange}");
        PlaybackLog.Write("stream", "response", new { source = $"stream://media/{_id}", status = _status, length = _length, range = _contentRange, mime = _mimeType });
        redirectUrl = string.Empty;
    }

    public bool ReadResponse(Stream dataOut, out int bytesRead, ICallback callback)
    {
        callback.Dispose();
        return CopyChunk(dataOut, out bytesRead);
    }

    /* 同步旧接口也实现（CEF 走哪条路径都能读到数据） */

    public bool Read(Stream dataOut, out int bytesRead, IResourceReadCallback callback)
    {
        callback.Dispose();
        return CopyChunk(dataOut, out bytesRead);
    }

    public bool Skip(long bytesToSkip, out long bytesSkipped, IResourceSkipCallback callback)
    {
        callback.Dispose();
        Trace.WriteLine($"stream:// {_id} skip={bytesToSkip}");
        bytesSkipped = -2;
        if (bytesToSkip < 0) return false;

        try
        {
            var acknowledged = Math.Min(bytesToSkip, _preSkippedBytes);
            _preSkippedBytes -= acknowledged;
            var pending = bytesToSkip - acknowledged;
            if (pending == 0)
            {
                bytesSkipped = bytesToSkip;
                return true;
            }

            if (_body == null || (_remaining >= 0 && pending > _remaining)) return false;
            if (_body.CanSeek)
            {
                if (pending > _body.Length - _body.Position) return false;
                _body.Seek(pending, SeekOrigin.Current);
            }
            else
            {
                // 上游若忽略 Range 返回 200，需要真正读掉前缀，不能假装成功。
                var buffer = new byte[64 * 1024];
                var left = pending;
                while (left > 0)
                {
                    var read = _body.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                    if (read == 0) return false;
                    left -= read;
                }
            }

            if (_remaining >= 0) _remaining -= pending;
            bytesSkipped = bytesToSkip;
            return true;
        }
        catch (Exception ex)
        {
            Log($"跳过数据失败 {_id}: {ex.Message}");
            return false;
        }
    }

    public void Cancel()
    {
        Trace.WriteLine($"stream:// {_id} cancel read={_bytesRead} remaining={_remaining}");
        PlaybackLog.Write("stream", "request_cancel", new { source = $"stream://media/{_id}", bytesRead = _bytesRead, remaining = _remaining });
        Dispose();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        try
        {
            _body?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _body = null;
        _response?.Dispose();
        _response = null;
        _client?.Dispose();
        _client = null;
    }
}
