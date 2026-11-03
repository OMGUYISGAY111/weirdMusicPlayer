using System;
using System.Diagnostics;
using System.IO;

namespace MusicPlayer.Host;

/// <summary>
/// 启动参数：
///   --dev                     加载 Vite dev server（127.0.0.1:5173），热更新
///   --url &lt;url&gt;             直接指定要加载的地址
///   --web-root &lt;dir&gt;        指定前端产物目录（含 index.html）
///   --user-data &lt;dir&gt;       指定数据目录（CEF profile / 缓存 / localStorage）
///   --pick-user-data          启动时强制弹框重选数据目录（选了才记住）
///   --debug-port &lt;port&gt;     指定远程调试端口（dev 默认 9222；被占用时换一个）
///   --debug-ui                打开 UI 诊断模式：把交互事件时间线写进 logs（排查「只有别人机器上才出问题」）
///
/// 数据目录怎么定，见 <see cref="DataLocation"/>：命令行 / 环境变量 / userdata.txt /
/// portable.txt / 默认 D:\MusicPlayerData / 首次启动弹框选择 / LOCALAPPDATA 兜底。
///
/// 显式指定（命令行、环境变量）的路径不可用会直接报错；默认位置不可用时会静默回退，
/// 回退结果会显示在页面「运行环境」卡片里。相对路径按 exe 所在目录解析（不是当前工作目录）。
/// </summary>
internal sealed class StartupOptions
{
    /// <summary>
    /// 需要用户挑数据目录时的回调：返回选中的路径，返回 null / 空表示用户放弃（沿用默认）。
    /// 由 Program 接上 WinForms 对话框；命令行工具传 null 就完全不弹框。
    /// </summary>
    public delegate string? DirectoryPicker(string suggestedPath, string reason, bool firstRun);

    public bool Dev { get; private init; }

    public string? Url { get; private init; }

    public string? WebRoot { get; private init; }

    public required string UserDataDir { get; init; }

    /// <summary>数据目录来自哪里，便于排查（显示在宿主信息里）</summary>
    public string UserDataSource { get; private init; } = "default";

    /// <summary>可选代理（例如 http://127.0.0.1:7897）：同时给 CEF 和 yt-dlp 用</summary>
    public string? Proxy { get; private init; }

    /// <summary>远程调试端口；null 表示 dev 时用默认 9222，非 dev 关闭</summary>
    public int? DebugPort { get; private init; }

    /// <summary>UI 诊断模式（`--debug-ui`）：前端据此打开交互事件时间线，写进 logs</summary>
    public bool DebugUi { get; private init; }

    public static StartupOptions Parse(string[] args, DirectoryPicker? pickDirectory = null)
    {
        var dev = false;
        string? url = null;
        string? webRoot = null;
        string? userData = null;
        string? proxy = null;
        int? debugPort = null;
        var pickUserData = false;
        var debugUi = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dev":
                    dev = true;
                    break;
                case "--pick-user-data":
                    pickUserData = true;
                    break;
                case "--debug-ui":
                    debugUi = true;
                    break;
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--web-root" when i + 1 < args.Length:
                    webRoot = args[++i];
                    break;
                case "--user-data" when i + 1 < args.Length:
                    userData = args[++i];
                    break;
                case "--proxy" when i + 1 < args.Length:
                    proxy = args[++i];
                    break;
                case "--debug-port" when i + 1 < args.Length:
                    debugPort = ParsePort(args[++i]);
                    break;
                default:
                    if (arg.StartsWith("--url=", StringComparison.Ordinal))
                    {
                        url = arg["--url=".Length..];
                    }
                    else if (arg.StartsWith("--web-root=", StringComparison.Ordinal))
                    {
                        webRoot = arg["--web-root=".Length..];
                    }
                    else if (arg.StartsWith("--user-data=", StringComparison.Ordinal))
                    {
                        userData = arg["--user-data=".Length..];
                    }
                    else if (arg.StartsWith("--debug-port=", StringComparison.Ordinal))
                    {
                        debugPort = ParsePort(arg["--debug-port=".Length..]);
                    }
                    break;
            }
        }

        var (userDataDir, source) = ResolveUserDataDir(userData, pickUserData, pickDirectory);

        return new StartupOptions
        {
            Dev = dev,
            Url = url,
            WebRoot = webRoot,
            UserDataDir = userDataDir,
            UserDataSource = source,
            Proxy = proxy,
            DebugPort = debugPort,
            DebugUi = debugUi,
        };
    }

    private static int? ParsePort(string raw)
    {
        return int.TryParse(raw.Trim(), out var port) && port is > 0 and < 65536 ? port : null;
    }

    private static (string Path, string Source) ResolveUserDataDir(
        string? fromArgs,
        bool forcePick,
        DirectoryPicker? pick)
    {
        // 1. 命令行：显式指定，不可用就报错，不偷偷换位置
        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return (DataLocation.Prepare(DataLocation.Normalize(fromArgs)), "命令行 --user-data");
        }

        // 2. 环境变量：同上
        var fromEnv = Environment.GetEnvironmentVariable("MUSICPLAYER_USER_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return (DataLocation.Prepare(DataLocation.Normalize(fromEnv)), "环境变量 MUSICPLAYER_USER_DATA");
        }

        // 3. 用户在设置面板 / 首次启动时选过、记在 userdata.txt 里的目录
        //    先把文件读出来（--pick-user-data 时也要读：用户可能在弹框里点取消，那就该沿用记住的）
        var savedRaw = DataLocation.ReadSavedPath();
        string? saved = null;
        var savedError = string.Empty;
        if (!string.IsNullOrWhiteSpace(savedRaw))
        {
            saved = DataLocation.Normalize(savedRaw);
            if (!forcePick && DataLocation.TryPrepare(saved, out savedError))
            {
                return (saved, "自定义目录（userdata.txt）");
            }

            // 记过的目录没了（U 盘拔掉、盘符变了）：下面重新问一次，别直接启动失败
            Debug.WriteLine($"userdata.txt 里的目录不可用：{savedError}");
        }

        // 4. 便携模式：exe 旁边的 portable.txt（内容随意，只要文件存在）
        if (!forcePick && DataLocation.PortableEnabled)
        {
            return (DataLocation.Prepare(DataLocation.PortableDataPath), "便携模式 portable.txt");
        }

        // 5. 默认位置已经存在且可写：老用户直接沿用，不弹框打扰
        if (!forcePick
            && Directory.Exists(DataLocation.DefaultPath)
            && DataLocation.TryPrepare(DataLocation.DefaultPath, out _))
        {
            return (DataLocation.DefaultPath, "默认位置");
        }

        // 6. 真·首次启动、默认位置不可用、或用户主动要求重选 -> 弹框让用户选
        var reason = forcePick
            ? "选择数据目录：播放列表、缓存、日志和设置都存在这里。取消则沿用当前目录。"
            : saved != null
                ? $"上次选择的目录不可用，请重新选择。\n{savedError}"
                : $"{DataLocation.DefaultPath} 还不存在，先选一个存放数据的位置。";

        var picked = AskAndSave(pick, saved ?? DataLocation.DefaultPath, reason, firstRun: saved == null && !forcePick);
        if (picked != null)
        {
            return picked.Value;
        }

        // 7. 没选（没有对话框，或用户点了取消）：记住的目录 > 默认位置 > LOCALAPPDATA 兜底
        if (saved != null && DataLocation.TryPrepare(saved, out _))
        {
            return (saved, "自定义目录（userdata.txt）");
        }

        if (DataLocation.TryPrepare(DataLocation.DefaultPath, out _))
        {
            return (DataLocation.DefaultPath, "默认位置");
        }

        if (DataLocation.TryPrepare(DataLocation.FallbackPath, out _))
        {
            return (DataLocation.FallbackPath,
                $"{DataLocation.DefaultPath} 不可用，已回退 LOCALAPPDATA");
        }

        throw new InvalidOperationException(
            $"没有任何可用的数据目录：{DataLocation.DefaultPath} 和 {DataLocation.FallbackPath} 都不可写。\n" +
            "请用 --user-data <目录> 指定一个可写的目录，或去掉 portable.txt / userdata.txt 后重试。");
    }

    /// <summary>弹框问用户；选完立刻校验并记住。没对话框、用户取消、路径不可用都返回 null。</summary>
    private static (string Path, string Source)? AskAndSave(
        DirectoryPicker? pick,
        string suggested,
        string reason,
        bool firstRun)
    {
        if (pick == null)
        {
            return null;
        }

        string? chosen;
        try
        {
            chosen = pick(suggested, reason, firstRun);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"选择数据目录失败：{ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(chosen))
        {
            return null;
        }

        var path = DataLocation.Prepare(DataLocation.Normalize(chosen));
        var remembered = DataLocation.SavePath(path);

        // 记不住也照用（exe 在无写权限目录时常这样），来源标签里体现出来
        return (path, remembered ? "自定义目录（已记住）" : "自定义目录（本次，未记住）");
    }
}
