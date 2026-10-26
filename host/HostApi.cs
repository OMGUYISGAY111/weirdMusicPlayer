using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using CefSharp;

namespace MusicPlayer.Host;

/// <summary>
/// 暴露给前端的 C# 接口，对应 renderer/src/bridge/host.ts 里的 CefHostApi。
/// CefSharp 会把方法名转成 camelCase：GetHostInfo -> getHostInfo。
/// </summary>
internal sealed class HostApi
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly MainForm _form;

    public HostApi(MainForm form)
    {
        _form = form;
    }

    /// <summary>返回 JSON 字符串，前端负责解析（避免跨进程类型映射问题）</summary>
    public string GetHostInfo()
    {
        var assembly = typeof(HostApi).Assembly;
        var info = new
        {
            appName = "音乐播放器（ver2 · app端）",
            hostVersion = assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            cefVersion = $"CefSharp {Cef.CefSharpVersion} / CEF {Cef.CefVersion}",
            chromiumVersion = Cef.ChromiumVersion,
            platform = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            dotnetVersion = RuntimeInformation.FrameworkDescription,
            isDev = _form.Options.Dev,
            userDataPath = _form.Options.UserDataDir,
            userDataSource = _form.Options.UserDataSource,
        };

        return JsonSerializer.Serialize(info, JsonOptions);
    }

    /// <summary>往返调用演示</summary>
    public string Echo(string text)
    {
        var payload = text ?? string.Empty;
        return $"C# 宿主已收到：\"{payload}\"\n长度 {payload.Length} 字符 · 宿主 PID {Environment.ProcessId}";
    }

    public void Minimize()
    {
        OnUiThread(() => _form.WindowState = FormWindowState.Minimized);
    }

    public void ToggleMaximize()
    {
        OnUiThread(() =>
            _form.WindowState = _form.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized);
    }

    public void CloseWindow()
    {
        OnUiThread(() => _form.Close());
    }

    public void OpenDevTools()
    {
        OnUiThread(() => _form.Browser.ShowDevTools());
    }

    public void SetZoomLevel(double level)
    {
        OnUiThread(() => _form.Browser.SetZoomLevel(level));
    }

    public void OpenUserDataFolder()
    {
        var dir = _form.Options.UserDataDir;
        OnUiThread(() => Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dir}\"",
            UseShellExecute = true,
        }));
    }

    /// <summary>CEF 的 JS 回调不在 WinForms UI 线程上，改窗口状态必须切回去</summary>
    private void OnUiThread(Action action)
    {
        if (_form.IsDisposed)
        {
            return;
        }

        if (_form.InvokeRequired)
        {
            _form.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
