using System.Collections.Specialized;
using System.Reflection;
using CefSharp;
using CefSharp.Callback;

internal static class StreamProxyChecks
{
    public static void Run(Assembly assembly, string streamUrl, string path)
    {
        var expected = File.ReadAllBytes(path);
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
