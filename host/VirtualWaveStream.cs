using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MusicPlayer.Host;

/// <summary>
/// 可按字节 seek 的虚拟 WAV：仅固定头部在内存中，PCM 数据由 FFmpeg 按需生成。
/// Range 偏移换算成采样时间，跳转时重启解码器，不下载整首、不创建媒体文件。
/// </summary>
internal sealed class VirtualWaveStream : Stream
{
    internal const int SampleRate = 48000;
    internal const int FrameSize = 4; // 16 bit, stereo
    internal const int HeaderSize = 44;
    private readonly RemoteAudioTrack _track;
    private readonly string _ffmpeg;
    private readonly byte[] _header;
    private readonly long _frames;
    private readonly CancellationTokenSource _cancel = new();
    private Process? _process;
    private Stream? _pcm;
    private long _position;
    private long _decodedBytes;
    private bool _cleanEof;
    private int _decoderReportedError;
    private int _decoderGeneration;
    private int _networkError;
    private bool _disposed;

    public VirtualWaveStream(RemoteAudioTrack track, string ffmpeg)
    {
        _track = track;
        _ffmpeg = ffmpeg;
        if (!double.IsFinite(track.Duration) || track.Duration <= 0 || track.Duration * SampleRate * FrameSize > uint.MaxValue - 36)
            throw new InvalidOperationException("实时转码需要有效时长，且单个音轨不能超过约 6 小时。");
        _frames = (long)Math.Round(track.Duration * SampleRate);
        _header = CreateHeader(checked((uint)(_frames * FrameSize)));
    }

    private static byte[] CreateHeader(uint dataLength)
    {
        var header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataLength + 36);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), SampleRate * FrameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), FrameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataLength);
        return header;
    }

    private void StartDecoder(long byteOffset)
    {
        var firstFrame = byteOffset / FrameSize;
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // B站直连，YouTube 与解析时使用相同出口。
        start.Environment.Remove("http_proxy"); start.Environment.Remove("https_proxy");
        start.Environment.Remove("HTTP_PROXY"); start.Environment.Remove("HTTPS_PROXY");
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror" }) start.ArgumentList.Add(arg);
        if (Uri.TryCreate(_track.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            foreach (var arg in new[] { "-rw_timeout", "20000000", "-http_proxy", _track.Proxy ?? "" }) start.ArgumentList.Add(arg);
            var headers = string.Concat(_track.Headers.Where(pair => !pair.Key.Contains('\r') && !pair.Key.Contains('\n')
                    && !pair.Value.Contains('\r') && !pair.Value.Contains('\n'))
                .Select(pair => $"{pair.Key}: {pair.Value}\r\n"));
            if (headers.Length > 0) { start.ArgumentList.Add("-headers"); start.ArgumentList.Add(headers); }
            var cookies = string.Concat(_track.Cookies.GetCookies(uri).Cast<System.Net.Cookie>()
                .Select(cookie => $"{cookie.Name}={cookie.Value}; domain={cookie.Domain}; path={cookie.Path};{(cookie.Secure ? " secure;" : "")}\n"));
            if (cookies.Length > 0) { start.ArgumentList.Add("-cookies"); start.ArgumentList.Add(cookies); }
        }
        foreach (var arg in new[] { "-ss", (firstFrame / (double)SampleRate).ToString("0.#########", CultureInfo.InvariantCulture),
            "-i", _track.Url, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-af", $"atrim=end_sample={_frames - firstFrame}", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(arg);
        var generation = Interlocked.Increment(ref _decoderGeneration);
        _process = Process.Start(start) ?? throw new IOException("实时音频解码器启动失败。");
        PlaybackLog.Write("decoder", "started", new { trackId = _track.Id, platform = _track.SourceKind,
            decoderPid = _process.Id, startSeconds = firstFrame / (double)SampleRate, expectedFrames = _frames - firstFrame });
        // 异步排空错误管道，避免阻塞；不把可能包含签名 URL/Cookie 的诊断写入日志。
        _process.ErrorDataReceived += (_, e) =>
        {
            if (generation == Volatile.Read(ref _decoderGeneration) && !string.IsNullOrWhiteSpace(e.Data))
            {
                PlaybackLog.Write("decoder", "stderr", new { trackId = _track.Id, message = e.Data }, "error");
                Interlocked.Exchange(ref _decoderReportedError, 1);
                if (new[] { "HTTP error", "Server returned", "Connection", "timed out", "Input/output error", "End of file" }
                    .Any(text => e.Data.Contains(text, StringComparison.OrdinalIgnoreCase))) Interlocked.Exchange(ref _networkError, 1);
            }
        };
        _process.BeginErrorReadLine();
        _pcm = _process.StandardOutput.BaseStream;
        for (var i = 0L; i < byteOffset % FrameSize; i++)
        {
            var single = new byte[1];
            if (ReadPcm(single, 0, 1) == 0) throw new IOException("实时音频未成功解码，请重新解析歌曲。");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        var total = 0;
        if (_position < HeaderSize)
        {
            var head = (int)Math.Min(count, HeaderSize - _position);
            Array.Copy(_header, _position, buffer, offset, head);
            _position += head; offset += head; count -= head; total += head;
        }
        var wanted = (int)Math.Min(count, Length - _position);
        if (wanted <= 0) return total;
        if (_pcm == null) StartDecoder(_position - HeaderSize);
        var read = _cleanEof ? 0 : ReadPcm(buffer, offset, wanted);
        if (read == 0)
        {
            if (!_cleanEof)
            {
                _process!.WaitForExitAsync(_cancel.Token).WaitAsync(TimeSpan.FromSeconds(3), _cancel.Token).GetAwaiter().GetResult();
                // WaitForExit 还会等待异步 stderr 事件排空；只保留失败标志，不记录敏感 URL。
                _process.WaitForExit();
                if (_process.ExitCode != 0 || Volatile.Read(ref _decoderReportedError) != 0)
                    throw new AudioStreamException("音频流加载或解码失败。", Volatile.Read(ref _networkError) != 0);
                // 仅容忍容器时长与末尾采样不超过 250ms 的误差。
                // 没解出音频、或中途断流时禁止用静音补满，否则进度会在无声时继续走。
                if (_decodedBytes == 0 || Length - _position > SampleRate * FrameSize / 4)
                    throw new AudioStreamException("音频流提前结束，未收到完整音频。", _decodedBytes > 0);
                _cleanEof = true;
            }
            Array.Clear(buffer, offset, wanted);
            read = wanted;
        }
        _position += read;
        return total + read;
    }

    private int ReadPcm(byte[] buffer, int offset, int count)
    {
        var read = _pcm!.ReadAsync(buffer.AsMemory(offset, count), _cancel.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), _cancel.Token).GetAwaiter().GetResult();
        _decodedBytes += read;
        return read;
    }

    private void StopDecoder()
    {
        Interlocked.Increment(ref _decoderGeneration);
        var process = _process;
        if (process != null) PlaybackLog.Write("decoder", "stopped", new { trackId = _track.Id, decodedBytes = _decodedBytes,
            position = _position, expectedLength = Length });
        _process = null;
        _pcm = null;
        _decodedBytes = 0;
        _cleanEof = false;
        Interlocked.Exchange(ref _decoderReportedError, 0);
        Interlocked.Exchange(ref _networkError, 0);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.StandardOutput.Dispose(); } catch { }
        process.Dispose();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => -1 };
        if (position < 0 || position > Length) throw new IOException("音频跳转超出范围。");
        if (position != _position) StopDecoder();
        return _position = position;
    }
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.Cancel();
        StopDecoder();
        base.Dispose(disposing);
    }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => HeaderSize + _frames * FrameSize;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
