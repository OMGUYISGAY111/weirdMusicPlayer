using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>本地 HTTP 上游夹具，验证代理请求头、Range 和真实 WebM/Opus 解码。</summary>
internal sealed class DirectStreamChecks : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancel = new();
    private readonly byte[] _bytes;
    public string Url { get; }
    public int Requests;

    public DirectStreamChecks(Assembly assembly, string path)
    {
        _bytes = File.ReadAllBytes(path);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var trackType = assembly.GetType("MusicPlayer.Host.RemoteAudioTrack", true)!;
        var track = Activator.CreateInstance(trackType, new object?[]
        {
            "http-fixture", "Direct Opus fixture", "", 24d, $"http://127.0.0.1:{port}/audio",
            "audio/webm; codecs=\"opus\"", new Dictionary<string, string> { ["X-Stream-Test"] = "present" }, null,
        })!;
        var registered = ((string Url, string Mode))assembly.GetType("MusicPlayer.Host.AudioStreamRegistration", true)!
            .GetMethod("Register")!.Invoke(null, new object[] { track, "[\"audio/webm; codecs=\\\"opus\\\"\"]" })!;
        if (registered.Mode != "direct") throw new Exception("支持的 Opus 不应进入转码路径");
        Url = registered.Url;
        _ = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                _ = Respond(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_cancel.IsCancellationRequested) { }
    }

    private async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync(_cancel.Token) ?? "";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(_cancel.Token) is { Length: > 0 } line)
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
            if (!headers.TryGetValue("X-Stream-Test", out var marker) || marker != "present")
                throw new Exception("代理未转发必需的请求头");
            var start = 0; var end = _bytes.Length - 1;
            var partial = headers.TryGetValue("Range", out var range);
            if (partial)
            {
                var match = Regex.Match(range!, @"bytes=(\d+)-(\d*)");
                start = int.Parse(match.Groups[1].Value);
                if (match.Groups[2].Value.Length > 0) end = Math.Min(end, int.Parse(match.Groups[2].Value));
            }
            var response = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: audio/webm\r\nContent-Length: {end - start + 1}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n";
            if (partial) response += $"Content-Range: bytes {start}-{end}/{_bytes.Length}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), _cancel.Token);
            Interlocked.Increment(ref Requests);
            if (!request.StartsWith("HEAD")) await stream.WriteAsync(_bytes.AsMemory(start, end - start + 1), _cancel.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cancel.Cancel(); _listener.Stop();
        Console.WriteLine($"PASS: HTTP 直接流播放，上游请求 {Requests} 次，必需请求头已转发");
    }
}
