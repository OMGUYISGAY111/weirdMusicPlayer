using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace MusicPlayer.Host;

internal sealed class MainForm : Form
{
    private readonly ChromiumWebBrowser _browser;

    public MainForm(WebTarget target, StartupOptions options)
    {
        Options = options;

        Text = options.Dev ? "音乐播放器 · Hello CEF [dev]" : "音乐播放器 · Hello CEF";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 15, 19);
        Font = new Font("Microsoft YaHei UI", 9F);

        // 屏幕小的时候别把窗口撑出屏幕
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        ClientSize = new Size(
            Math.Min(1180, Math.Max(760, workArea.Width - 80)),
            Math.Min(780, Math.Max(520, workArea.Height - 80)));
        MinimumSize = new Size(720, 480);

        _browser = new ChromiumWebBrowser(target.Url, requestContext: null)
        {
            Dock = DockStyle.Fill,
        };

        // 注册给前端的 hostApi：CefSharp 154 是异步绑定，
        // 前端需先 await CefSharp.BindObjectAsync('hostApi') 才会出现 window.hostApi。
        // 方法名从 PascalCase 自动转 camelCase（GetHostInfo -> getHostInfo）。
        _browser.JavascriptObjectRepository.Register("hostApi", new HostApi(this), BindingOptions.DefaultBinder);

        // 把页面 console 输出转到调试输出，dev 下排错用
        _browser.ConsoleMessage += (_, e) =>
        {
            Debug.WriteLine($"[js:{e.Level}] {e.Message} ({e.Source}:{e.Line})");
            if (e.Level is LogSeverity.Error or LogSeverity.Warning)
                PlaybackLog.Write("renderer.console", "message", new { message = e.Message, source = e.Source, line = e.Line },
                    e.Level == LogSeverity.Error ? "error" : "warn");
        };

        Controls.Add(_browser);
    }

    public StartupOptions Options { get; }

    public ChromiumWebBrowser Browser => _browser;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _browser.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"释放 ChromiumWebBrowser 失败: {ex}");
        }

        base.OnFormClosed(e);
    }
}
