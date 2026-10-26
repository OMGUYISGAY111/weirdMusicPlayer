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
///   --debug-port &lt;port&gt;     指定远程调试端口（dev 默认 9222；被占用时换一个）
///
/// 数据目录优先级：
///   1. --user-data &lt;dir&gt;
///   2. 环境变量 MUSICPLAYER_USER_DATA
///   3. exe 同目录存在 portable.txt  -> .\data（便携模式）
///   4. 默认 D:\MusicPlayerData（D 盘不可用时回退 %LOCALAPPDATA%\MusicPlayer\ver2）
///
/// 显式指定（1/2）的路径不可用会直接报错；只有内置默认值才会静默回退，
/// 回退结果会显示在页面「运行环境」卡片里。
///
/// 相对路径按 exe 所在目录解析（不是当前工作目录），方便便携部署。
/// </summary>
internal sealed class StartupOptions
{
    /// <summary>默认数据目录</summary>
    private const string DefaultUserDataDir = @"D:\MusicPlayerData";
    public bool Dev { get; private init; }

    public string? Url { get; private init; }

    public string? WebRoot { get; private init; }

    public required string UserDataDir { get; init; }

    /// <summary>数据目录来自哪里，便于排查（显示在宿主信息里）</summary>
    public string UserDataSource { get; private init; } = "default";

    /// <summary>远程调试端口；null 表示 dev 时用默认 9222，非 dev 关闭</summary>
    public int? DebugPort { get; private init; }

    public static StartupOptions Parse(string[] args)
    {
        var dev = false;
        string? url = null;
        string? webRoot = null;
        string? userData = null;
        int? debugPort = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dev":
                    dev = true;
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

        var (userDataDir, source) = ResolveUserDataDir(userData);

        return new StartupOptions
        {
            Dev = dev,
            Url = url,
            WebRoot = webRoot,
            UserDataDir = userDataDir,
            UserDataSource = source,
            DebugPort = debugPort,
        };
    }

    private static int? ParsePort(string raw)
    {
        return int.TryParse(raw.Trim(), out var port) && port is > 0 and < 65536 ? port : null;
    }

    private static (string Path, string Source) ResolveUserDataDir(string? fromArgs)
    {
        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return (Normalize(fromArgs), "命令行 --user-data");
        }

        var fromEnv = Environment.GetEnvironmentVariable("MUSICPLAYER_USER_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return (Normalize(fromEnv), "环境变量 MUSICPLAYER_USER_DATA");
        }

        // 便携模式：exe 旁边的 portable.txt（内容随意，只要文件存在）
        if (File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            return (Prepare(System.IO.Path.Combine(AppContext.BaseDirectory, "data")), "便携模式 portable.txt");
        }

        // 默认放 D 盘。万一 D 盘不存在 / 不可写（换台机器、盘符被占用），
        // 回退到 LOCALAPPDATA 而不是直接启动失败；回退原因会写进来源标签，页面上一眼可见。
        try
        {
            return (Prepare(DefaultUserDataDir), "默认位置");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"默认数据目录不可用，回退 LOCALAPPDATA：{ex.Message}");

            var fallback = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicPlayer",
                "ver2");

            return (Prepare(fallback), $"{DefaultUserDataDir} 不可用，已回退 LOCALAPPDATA");
        }
    }

    /// <summary>展开环境变量，并把相对路径补成 exe 目录下的绝对路径</summary>
    private static string Normalize(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        var absolute = System.IO.Path.IsPathRooted(expanded)
            ? expanded
            : System.IO.Path.Combine(AppContext.BaseDirectory, expanded);

        return Prepare(System.IO.Path.GetFullPath(absolute));
    }

    /// <summary>建目录并做一次写测试，失败时给出带路径的清晰错误（比 CEF 内部的报错好懂）</summary>
    private static string Prepare(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法创建数据目录：{path}\n请检查盘符是否存在、是否有写权限。\n{ex.Message}", ex);
        }

        var probe = System.IO.Path.Combine(path, ".write-probe");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"数据目录不可写：{path}\n请换一个目录，或检查该目录的权限/只读属性。\n{ex.Message}", ex);
        }

        return path;
    }
}
