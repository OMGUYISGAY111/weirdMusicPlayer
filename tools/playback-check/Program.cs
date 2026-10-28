using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;

internal static class PlaybackCheck
{
    [STAThread]
    private static int Main(string[] args)
    {
        var remote = args.Length == 2 && args[0] is "bili" or "youtube";
        if (args.Length != 2 || !new[] { "dev", "prod", "url", "ranges", "transcode", "direct", "failure", "recovery", "bili", "youtube" }.Contains(args[0]) || (!remote && !File.Exists(args[1])))
        {
            Console.Error.WriteLine("用法：Playback.Check <dev|prod|url|ranges|transcode> <音频文件绝对路径>，或 <bili|youtube> <视频地址>");
            return 2;
        }

        Application.EnableVisualStyles();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Trace.AutoFlush = true;
        var assembly = Assembly.Load("MusicPlayer.Host");
        Type HostType(string name) => assembly.GetType("MusicPlayer.Host." + name, true)!;
        var profile = Path.Combine(Path.GetTempPath(), "MusicPlayerPlaybackCheck", Guid.NewGuid().ToString("N"));
        HostType("PlaybackLog").GetMethod("Initialize")!.Invoke(null, new object[] { profile, 2L * 1024 * 1024, 4 });
        var optionsArgs = new List<string> { "--user-data", profile, "--debug-port", "0" };
        if (args[0] == "dev") optionsArgs.Add("--dev");
        if (args[0] == "url") optionsArgs.AddRange(new[] { "--url", "http://127.0.0.1:5173/" });
        var options = HostType("StartupOptions").GetMethod("Parse")!.Invoke(null, new object[] { optionsArgs.ToArray() });
        var target = HostType("WebTarget").GetMethod("Resolve")!.Invoke(null, new[] { options });
        var url = (string)HostType("WebTarget").GetProperty("Url")!.GetValue(target)!;
        if (args[0] == "dev" && !url.StartsWith("http://"))
            throw new Exception("dev 验证需要先运行 pnpm dev:web");
        var settings = (CefSettings)HostType("Program")
            .GetMethod("BuildSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new[] { options, target })!;
        string streamUrl;
        if (remote)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            var task = (Task)(args[0] == "bili"
                ? HostType("BilibiliResolver").GetMethod("ResolveAsync")!.Invoke(null, new object[] { args[1], timeout.Token })!
                : HostType("YoutubeResolver").GetMethod("ResolveAsync")!.Invoke(null, new object?[] { args[1], HostType("SystemProxy").GetMethod("Detect")!.Invoke(null, null), timeout.Token, true })!);
            task.GetAwaiter().GetResult();
            var track = task.GetType().GetProperty("Result")!.GetValue(task)!;
            if (args[0] == "bili") RealtimeStreamChecks.ProbeAudio(assembly, track);
            var registration = ((string Url, string Mode))HostType("AudioStreamRegistration").GetMethod("Register")!
                .Invoke(null, new object[] { track, "[\"audio/webm; codecs=\\\"opus\\\"\"]" })!;
            streamUrl = registration.Url;
            Console.WriteLine($"Resolved {args[0]}: mode={registration.Mode}");
        }
        else streamUrl = (string)HostType("StreamRegistry").GetMethod("RegisterLocalFile")!
            .Invoke(null, new object[] { Path.GetFullPath(args[1]) })!;

        if (Cef.Initialize(settings) != true) throw new Exception("CEF 初始化失败");
        if (!remote) StreamProxyChecks.Run(assembly, streamUrl, args[1]);
        if (args[0] == "ranges") { Cef.Shutdown(); return 0; }
        if (args[0] == "transcode") streamUrl = RealtimeStreamChecks.Run(assembly, Path.GetFullPath(args[1]));
        if (args[0] == "recovery")
        {
            var track = Activator.CreateInstance(HostType("RemoteAudioTrack"), new object?[]
            { "recovery", "Recovery fixture", "", 24d, Path.GetFullPath(args[1]), "audio/wav", new Dictionary<string, string>(), null })!;
            streamUrl = (string)HostType("StreamRegistry").GetMethod("RegisterTranscoded")!.Invoke(null, new[] { track })!;
        }
        if (args[0] == "failure")
        {
            // 输入夹具只有 24 秒，故意声明 48 秒，模拟中途丢失音频。
            var incomplete = Activator.CreateInstance(HostType("RemoteAudioTrack"), new object?[]
            { "broken", "Incomplete fixture", "", 48d, Path.GetFullPath(args[1]), "audio/wav", new Dictionary<string, string>(), null })!;
            streamUrl = (string)HostType("StreamRegistry").GetMethod("RegisterTranscoded")!.Invoke(null, new[] { incomplete })!;
        }
        using var direct = args[0] == "direct" ? new DirectStreamChecks(assembly, args[1]) : null;
        if (direct != null) streamUrl = direct.Url;
        var exitCode = 1;
        using (var form = new Form { Opacity = 0, ShowInTaskbar = false })
        using (var browser = new ChromiumWebBrowser(url))
        {
            browser.JavascriptObjectRepository.Register("streamProbe", new StreamFailureProbe(assembly), BindingOptions.DefaultBinder);
            // 真实页面会分配并回收对象；同时覆盖 CEF 回调被 GC 回收后的资源生命周期。
            using var gcTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            gcTimer.Tick += (_, _) => { GC.Collect(); GC.WaitForPendingFinalizers(); };
            gcTimer.Start();
            form.Controls.Add(browser);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.LoadingStateChanged += (_, e) => { if (!e.IsLoading) loaded.TrySetResult(); };
            form.Shown += async (_, _) =>
            {
                try
                {
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    if (args[0] == "recovery")
                    {
                        await RecoveryPlaybackChecks.Run(browser, streamUrl);
                        exitCode = 0;
                        return;
                    }
                    if (args[0] == "failure")
                    {
                        var failure = await browser.EvaluateScriptAsPromiseAsync($$"""
                            return new Promise(async (resolve, reject) => {
                              const audio = new Audio(); audio.muted = true; audio.preload = 'auto';
                              let done = false, monitor;
                              const timer = setTimeout(() => { clearInterval(monitor); audio.pause(); reject('音频缺失未报错，可能仍在用静音填充'); }, 20000);
                              const fail = async (source) => {
                                if (done) return; done = true;
                                clearTimeout(timer); clearInterval(monitor); audio.pause();
                                const time = audio.currentTime;
                                await new Promise(r => setTimeout(r, 500));
                                if (audio.currentTime > time + 0.01) { reject('报错后进度仍在推进'); return; }
                                resolve(JSON.stringify({ source, code: audio.error?.code, time, paused: audio.paused }));
                              };
                              audio.onerror = () => fail('media');
                              try { await CefSharp.BindObjectAsync('streamProbe'); } catch (error) { reject(String(error)); return; }
                              monitor = setInterval(async () => {
                                if (await streamProbe.getStreamError({{JsonSerializer.Serialize(streamUrl)}})) void fail('host');
                              }, 100);
                              audio.oncanplay = () => { audio.playbackRate = 8; void audio.play().catch(() => {}); };
                              audio.src = {{JsonSerializer.Serialize(streamUrl)}}; audio.load();
                            });
                            """, TimeSpan.FromSeconds(25));
                        if (!failure.Success) throw new Exception(failure.Message);
                        Console.WriteLine("PASS: 真实 CEF 检出缺失音频并停止进度 " + failure.Result);
                        exitCode = 0;
                        return;
                    }
                    var result = await browser.EvaluateScriptAsPromiseAsync($$"""
                        return new Promise((resolve, reject) => {
                          const audio = new Audio();
                          audio.preload = 'metadata';
                          audio.muted = true;
                          const timer = setTimeout(() => reject(`音频加载/播放超时 time=${audio.currentTime} ready=${audio.readyState}`), 110000);
                          audio.onerror = () => {
                            clearTimeout(timer);
                            reject(`code=${audio.error?.code} ${audio.error?.message}`);
                          };
                          audio.onloadedmetadata = async () => {
                            try {
                              await audio.play();
                              const delay = (ms) => new Promise(r => setTimeout(r, ms));
                              const seek = (time) => new Promise(r => {
                                audio.onseeked = () => { audio.onseeked = null; r(); };
                                audio.currentTime = time;
                              });
                              await delay(12000);
                              const played = audio.currentTime;
                              if (played < 10) throw new Error(`连续播放卡住 time=${played}`);
                              await seek(audio.duration * 0.6);
                              const seekTime = audio.currentTime;
                              await delay(4000);
                              if (audio.currentTime < seekTime + 3) throw new Error('向前跳转后卡住');
                              await seek(5);
                              await delay(4000);
                              if (audio.currentTime < 8) throw new Error('向后跳转后卡住');
                              audio.pause();
                              await audio.play();
                              // 再从头以 8 倍速解码整首歌，覆盖文件尾与 ended，避免只测到首个缓冲块。
                              await seek(0);
                              audio.playbackRate = 8;
                              await new Promise(r => { audio.onended = r; });
                              clearTimeout(timer);
                              resolve(JSON.stringify({ origin: location.origin, src: audio.currentSrc,
                                duration: audio.duration, played, seekTime, ended: audio.ended,
                                error: audio.error?.code ?? 0 }));
                            } catch (error) { clearTimeout(timer); reject(String(error)); }
                          };
                          audio.src = {{JsonSerializer.Serialize(streamUrl)}};
                          audio.load();
                        });
                        """, TimeSpan.FromSeconds(115));
                    if (!result.Success) throw new Exception(result.Message);
                    var json = (string)result.Result;
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement;
                    if (data.GetProperty("duration").GetDouble() <= 0 || data.GetProperty("played").GetDouble() < 10
                        || !data.GetProperty("ended").GetBoolean() || data.GetProperty("error").GetInt32() != 0
                        || Math.Abs(data.GetProperty("seekTime").GetDouble() - data.GetProperty("duration").GetDouble() * 0.6) > 1)
                        throw new Exception("播放/seek 检查失败：" + json);
                    Console.WriteLine("PASS " + args[0] + ": " + json);
                    exitCode = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("FAIL " + args[0] + ": " + ex.Message); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        Cef.Shutdown();
        ((Task)HostType("PlaybackLog").GetMethod("FlushAsync")!.Invoke(null, null)!).GetAwaiter().GetResult();
        if (args[0] == "recovery" && exitCode == 0)
        {
            var log = Path.Combine(profile, "logs", "playback.jsonl");
            var events = File.ReadLines(log).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                if (!events.Any(doc => doc.RootElement.GetProperty("event").GetString() == "recovery_success")
                    || !events.Any(doc => doc.RootElement.GetProperty("event").GetString() == "started"))
                    throw new Exception("前后端诊断日志未完整落盘");
                Console.WriteLine("PASS: 真实 CEF 的前端恢复事件与后台解码日志已落盘");
            }
            finally { foreach (var doc in events) doc.Dispose(); }
        }
        HostType("PlaybackLog").GetMethod("Shutdown")!.Invoke(null, null);
        return exitCode;
    }
}

public sealed class StreamFailureProbe(Assembly assembly)
{
    public void RecordPlaybackLog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        assembly.GetType("MusicPlayer.Host.PlaybackLog", true)!.GetMethod("Write")!.Invoke(null, new object[]
        { "renderer.test", doc.RootElement.GetProperty("event").GetString()!, doc.RootElement.GetProperty("data").Clone(), "info" });
    }
    public string GetStreamError(string url) => (string)assembly.GetType("MusicPlayer.Host.StreamRegistry", true)!
        .GetMethod("GetError")!.Invoke(null, new object[] { url })!;
    public void FailStream(string url) => assembly.GetType("MusicPlayer.Host.StreamRegistry", true)!
        .GetMethod("MarkFailed")!.Invoke(null, new object[] { new Uri(url).AbsolutePath.Trim('/'), true });
    public string RenewStream(string url)
    {
        var registry = assembly.GetType("MusicPlayer.Host.StreamRegistry", true)!;
        var parameters = new object?[] { new Uri(url).AbsolutePath.Trim('/'), null };
        registry.GetMethod("TryGet")!.Invoke(null, parameters);
        var track = parameters[1]!.GetType().GetProperty("Track")!.GetValue(parameters[1])!;
        var next = (string)registry.GetMethod("RegisterTranscoded")!.Invoke(null, new[] { track })!;
        return JsonSerializer.Serialize(new { url = next });
    }
}
