using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using CefSharp;
using CefSharp.SchemeHandler;
using CefSharp.WinForms;

namespace MusicPlayer.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        StartupOptions options;
        WebTarget target;

        try
        {
            options = StartupOptions.Parse(args);
            PlaybackLog.Initialize(options.UserDataDir);
            PlaybackLog.Write("app", "startup", new { version = typeof(Program).Assembly.GetName().Version?.ToString(), dev = options.Dev,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = Environment.OSVersion.VersionString });
            target = WebTarget.Resolve(options);
        }
        catch (Exception ex)
        {
            ShowFatal("启动参数/前端资源解析失败", ex);
            return;
        }

        try
        {
            var settings = BuildSettings(options, target);
            if (Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null) is false)
            {
                throw new InvalidOperationException("Cef.Initialize 返回 false，CEF 内核未能启动。");
            }
        }
        catch (Exception ex)
        {
            ShowFatal("CEF 初始化失败", ex);
            return;
        }

        try
        {
            Application.ThreadException += (_, e) => { PlaybackLog.Exception("app", "ui_exception", e.Exception); ShowFatal("界面线程异常", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            { if (e.ExceptionObject is Exception error) { PlaybackLog.Exception("app", "unhandled_exception", error); PlaybackLog.Shutdown(); } };
            TaskScheduler.UnobservedTaskException += (_, e) => PlaybackLog.Exception("app", "unobserved_task", e.Exception);
            Application.Run(new MainForm(target, options));
        }
        catch (Exception ex)
        {
            ShowFatal("宿主运行时异常", ex);
        }
        finally
        {
            if (Cef.IsInitialized == true)
            {
                Cef.Shutdown();
            }
            PlaybackLog.Write("app", "shutdown");
            PlaybackLog.Shutdown();
        }
    }

    private static CefSettings BuildSettings(StartupOptions options, WebTarget target)
    {
        var cacheRoot = Path.Combine(options.UserDataDir, "Cache");
        Directory.CreateDirectory(cacheRoot);

        var settings = new CefSettings
        {
            RootCachePath = options.UserDataDir,
            CachePath = cacheRoot,
            Locale = "zh-CN",
            LogSeverity = LogSeverity.Disable,
            // 页面加载前与无背景色文档的底色，和前端 --bg 保持一致，避免白屏闪烁
            BackgroundColor = 0xFF0E0F13,
            UserAgentProduct = "MusicPlayer/0.1",
        };

        // 播放器必须能在没有用户手势的情况下自动播放
        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            // 显式代理：CEF 和 yt-dlp 用同一个（--proxy）。本机地址必须 bypass，
            // 否则 app:// 之外还要把 dev server / stream:// 本地文件代理坏掉。
            settings.CefCommandLineArgs["proxy-server"] = options.Proxy;
            settings.CefCommandLineArgs["proxy-bypass-list"] = "127.0.0.1;localhost;<local>";
        }

        if (options.Dev)
        {
            // dev 下可以用 chrome://inspect 连这个端口调试页面；
            // 9222 常被本机 Chrome 占用，可用 --debug-port 换一个。
            var port = options.DebugPort ?? 9222;

            if (!IsPortFree(port))
            {
                Debug.WriteLine($"远程调试端口 {port} 已被占用，改用 0（关闭）");

                port = 0;
            }

            settings.RemoteDebuggingPort = port;
        }
        else if (options.DebugPort is { } explicitPort)
        {
            settings.RemoteDebuggingPort = explicitPort;
        }

        if (target.UsesAppScheme)
        {
            // 自定义标准协议：比 file:// 干净（file:// 下 ES module 会被 CORS 拦掉），
            // 也比临时 HTTP 服务省事，后续给音乐播放器加本地媒体协议时可以照这个模式扩展。
            settings.RegisterScheme(new CefCustomScheme
            {
                SchemeName = WebTarget.SchemeName,
                DomainName = WebTarget.DomainName,
                SchemeHandlerFactory = new FolderSchemeHandlerFactory(
                    rootFolder: target.RootDirectory!,
                    schemeName: WebTarget.SchemeName,
                    hostName: WebTarget.DomainName,
                    defaultPage: "index.html",
                    resourceFileShare: FileShare.Read),
                IsStandard = true,
                IsSecure = true,
                IsCorsEnabled = true,
                IsFetchEnabled = true,
            });
        }

        // dev / --url 页面也使用 stream:// 播放解析后的本地音频。
        // 必须在所有启动模式下、Cef.Initialize 之前注册，否则媒体元素会报错误码 4。
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = "stream",
            DomainName = "media",
            SchemeHandlerFactory = new StreamProxySchemeHandlerFactory(),
            IsStandard = true,
            IsSecure = true,
            IsCorsEnabled = true,
            IsFetchEnabled = true,
        });

        return settings;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static void ShowFatal(string title, Exception ex)
    {
        PlaybackLog.Exception("app", "fatal", ex, new { title });
        PlaybackLog.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        MessageBox.Show(
            $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            $"{title} - 音乐播放器",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
