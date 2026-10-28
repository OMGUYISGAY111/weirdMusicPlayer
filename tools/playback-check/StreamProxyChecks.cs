using System.Collections.Specialized;
using System.Reflection;
using CefSharp;
using CefSharp.Callback;

internal static class StreamProxyChecks
{
    public static void Run(Assembly assembly, string streamUrl, string path)
    {
        var expected = File.ReadAllBytes(path);
        RunExpected(assembly, streamUrl, expected);
    }

    public static void RunExpected(Assembly assembly, string streamUrl, byte[] expected)
    {
        foreach (var (start, skip) in new[] { (0, 0), (65536, 65536), (expected.Length - 100, expected.Length - 100), (0, 100), (65536, 65636) })
        {
            var handlerType = assembly.GetType("MusicPlayer.Host.StreamProxyHandler", true)!;
            using var handler = (IResourceHandler)Activator.CreateInstance(handlerType,
                new object[] { new Uri(streamUrl).AbsolutePath.Trim('/') })!;
            using var request = Request.Create();
            request.Url = streamUrl;
            request.Method = "GET";
            request.Headers = new NameValueCollection { ["Range"] = $"bytes={start}-" };
            using var openCallback = new OpenCallback();
            if (!handler.Open(request, out var handled, openCallback) || !handled || !openCallback.IsDisposed)
                throw new Exception("Range 请求打开失败");

            using var skipCallback = new SkipCallback();
            if (!handler.Skip(skip, out var skipped, skipCallback) || skipped != skip || !skipCallback.IsDisposed)
                throw new Exception("Range 偏移确认失败");
            using var received = new MemoryStream();
            while (true)
            {
                using var output = new MemoryStream(new byte[64 * 1024]);
                using var readCallback = new ReadCallback();
                var more = handler.Read(output, out var read, readCallback);
                if (!readCallback.IsDisposed || read < 0) throw new Exception("读取失败或回调未释放");
                if (!more) break;
                received.Write(output.ToArray(), 0, read);
            }
            if (!received.ToArray().AsSpan().SequenceEqual(expected.AsSpan(skip)))
                throw new Exception($"Range {start} 返回内容错误（重复跳过或丢失字节）");
        }
        Console.WriteLine("PASS: Range 续传字节一致，回调按同步契约释放");
        foreach (var (range, status, offset, count) in new[]
        {
            ("bytes=32-63", 206, 32, 32),
            ("bytes=-100", 206, expected.Length - 100, 100),
            ($"bytes={expected.Length}-", 416, 0, 0),
            ("bytes=999999999999999999999999999-", 416, 0, 0),
            ("bytes=0-2,4-8", 416, 0, 0),
        })
        {
            var handlerType = assembly.GetType("MusicPlayer.Host.StreamProxyHandler", true)!;
            using var handler = (IResourceHandler)Activator.CreateInstance(handlerType, new object[] { new Uri(streamUrl).AbsolutePath.Trim('/') })!;
            using var request = Request.Create(); request.Url = streamUrl; request.Method = "GET";
            request.Headers = new NameValueCollection { ["Range"] = range };
            using var callback = new OpenCallback();
            if (!handler.Open(request, out var handled, callback) || !handled) throw new Exception("Range 打开失败");
            var actualStatus = (int)handlerType.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            var length = (long)handlerType.GetField("_length", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            if (actualStatus != status || length != count) throw new Exception($"Range {range} 状态或长度错误");
            using var output = new MemoryStream(new byte[1024]);
            using var readCallback = new ReadCallback();
            handler.Read(output, out var read, readCallback);
            if (read != count || !output.ToArray().AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count)))
                throw new Exception($"Range {range} 内容错误");
        }
        Console.WriteLine("PASS: 定长 Range、后缀 Range、越界和畸形 Range");
    }

    private sealed class OpenCallback : ICallback
    {
        public bool IsDisposed { get; private set; }
        public void Continue() => throw new Exception("同步请求不应使用异步回调");
        public void Cancel() => throw new Exception("同步请求不应使用异步回调");
        public void Dispose() => IsDisposed = true;
    }
    private sealed class SkipCallback : IResourceSkipCallback
    {
        public bool IsDisposed { get; private set; }
        public void Continue(long bytesSkipped) => throw new Exception("同步跳过不应使用异步回调");
        public void Dispose() => IsDisposed = true;
    }
    private sealed class ReadCallback : IResourceReadCallback
    {
        public bool IsDisposed { get; private set; }
        public void Continue(int bytesRead) => throw new Exception("同步读取不应使用异步回调");
        public void Dispose() => IsDisposed = true;
    }
}
