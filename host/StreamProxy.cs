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
internal sealed record StreamDescriptor(string Url, Dictionary<string, string> Headers, string? LocalPath);

internal static class StreamRegistry
{
    private static readonly ConcurrentDictionary<string, StreamDescriptor> Items = new();
    private static int _sequence;

    /// <summary>注册一个上游地址，返回页面可用的 stream:// 地址</summary>
    public static string Register(string url, string? headersJson = null)
    {
        var headers = ParseHeaders(headersJson);
        return Add(new StreamDescriptor(url, headers, null));
    }

    /// <summary>注册一个本地文件（例如 B站 转码后的 mp3），支持 Range/seek</summary>
    public static string RegisterLocalFile(string path)
    {
        var full = Path.GetFullPath(path);
        return Add(new StreamDescriptor(string.Empty, new Dictionary<string, string>(), full));
    }

    private static string Add(StreamDescriptor descriptor)
    {
        var id = Interlocked.Increment(ref _sequence).ToString("x");
        Items[id] = descriptor;
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

            _headOnly = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
            var range = request.Headers?["Range"];
            Trace.WriteLine($"stream:// {_id} open {request.Method} range={range ?? "none"}");

            if (!string.IsNullOrEmpty(descriptor.LocalPath))
            {
                return OpenLocalFile(descriptor.LocalPath!, range);
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

            var response = ProxyHttp.Client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();

            _response = response;
            _status = (int)response.StatusCode;
            _length = response.Content.Headers.ContentLength ?? -1;
            _remaining = _length;
            _contentRange = response.Content.Headers.ContentRange?.ToString();
            _preSkippedBytes = response.Content.Headers.ContentRange?.From ?? 0;
            _mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            if (!_headOnly)
            {
                _body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"上游失败 {_id}: {ex.GetType().Name} {ex.Message}");
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

        var info = new FileInfo(path);
        var total = info.Length;
        var start = 0L;
        var end = total - 1;

        if (!string.IsNullOrEmpty(range))
        {
            var match = Regex.Match(range, "bytes=(\\d*)-(\\d*)");
            if (match.Success)
            {
                if (match.Groups[1].Value.Length > 0)
                {
                    start = long.Parse(match.Groups[1].Value);
                }

                if (match.Groups[2].Value.Length > 0)
                {
                    end = Math.Min(long.Parse(match.Groups[2].Value), total - 1);
                }
            }
        }

        if (start > end || start >= total)
        {
            _status = 416;
            _length = 0;
            _remaining = 0;
            return true;
        }

        _status = string.IsNullOrEmpty(range) ? 200 : 206;
        _length = end - start + 1;
        _remaining = _length;
        _contentRange = string.IsNullOrEmpty(range) ? null : $"bytes {start}-{end}/{total}";
        _mimeType = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "application/octet-stream";

        if (_headOnly)
        {
            return true;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            var read = _body.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
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
        catch (Exception ex)
        {
            Log($"读取中断 {_id}: {ex.Message}");
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
        Dispose();
    }

    public void Dispose()
    {
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
    }
}
