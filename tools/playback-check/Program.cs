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
        if (args.Length != 2 || !new[] { "dev", "prod", "url", "ranges" }.Contains(args[0]) || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("用法：Playback.Check <dev|prod|url|ranges> <MP3文件绝对路径>");
            return 2;
        }

        Application.EnableVisualStyles();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Trace.AutoFlush = true;
        var assembly = Assembly.Load("MusicPlayer.Host");
        Type HostType(string name) => assembly.GetType("MusicPlayer.Host." + name, true)!;
        var profile = Path.Combine(Path.GetTempPath(), "MusicPlayerPlaybackCheck", Guid.NewGuid().ToString("N"));
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
        var streamUrl = (string)HostType("StreamRegistry").GetMethod("RegisterLocalFile")!
            .Invoke(null, new object[] { Path.GetFullPath(args[1]) })!;

        if (Cef.Initialize(settings) != true) throw new Exception("CEF 初始化失败");
        StreamProxyChecks.Run(assembly, streamUrl, args[1]);
        if (args[0] == "ranges") { Cef.Shutdown(); return 0; }
        var exitCode = 1;
        using (var form = new Form { Opacity = 0, ShowInTaskbar = false })
        using (var browser = new ChromiumWebBrowser(url))
        {
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
                    var result = await browser.EvaluateScriptAsPromiseAsync($$"""
                        return new Promise((resolve, reject) => {
                          const audio = new Audio();
                          audio.preload = 'metadata';
                          audio.muted = true;
                          const timer = setTimeout(() => reject(`音频加载/播放超时 time=${audio.currentTime} ready=${audio.readyState}`), 55000);
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
                        """, TimeSpan.FromSeconds(60));
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
        return exitCode;
    }
}
