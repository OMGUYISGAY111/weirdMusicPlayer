using System;
using System.IO;
using System.Text;

namespace MusicPlayer.Host;

/// <summary>
/// 数据目录（CEF profile / 播放列表 / 缓存 / 日志 / settings.json）的解析、校验与持久化。
///
/// 优先级从高到低：
///   1. 命令行 <c>--user-data &lt;dir&gt;</c>
///   2. 环境变量 <c>MUSICPLAYER_USER_DATA</c>
///   3. 用户自己选过的目录（exe 同目录的 userdata.txt，设置面板 / 首次启动写入）
///   4. 便携模式：exe 同目录有 <c>portable.txt</c> -> <c>exe\data</c>
///   5. 默认 <c>D:\MusicPlayerData</c>；真·首次启动时弹框让用户选，选了就写进 userdata.txt
///   6. 兜底 <c>%LOCALAPPDATA%\MusicPlayer\ver2</c>（D 盘不存在 / 不可写时）
///
/// 1、2 是「显式指定」，路径不可用会直接报错；3、5 记不住或不可用时可以让用户重新选。
/// </summary>
internal static class DataLocation
{
    /// <summary>默认数据目录</summary>
    public const string DefaultPath = @"D:\MusicPlayerData";

    /// <summary>用户选择的目录记在这个文件里（和 exe 同目录，方便整个文件夹拷走）</summary>
    public const string ConfigFileName = "userdata.txt";

    /// <summary>存在这个文件就走便携模式，数据放 exe\data</summary>
    public const string PortableMarkerName = "portable.txt";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    public static string PortableDataPath => Path.Combine(AppContext.BaseDirectory, "data");

    public static bool PortableEnabled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerName));

    /// <summary>默认位置和用户选择都不可用时的兜底（系统盘用户目录，CEF 写缓存没问题）</summary>
    public static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicPlayer", "ver2");

    /// <summary>
    /// 读 userdata.txt 里记的目录。文件不存在 / 只有注释 / 读失败都返回 null。
    /// 允许 <c>#</c> 开头的注释行和 <c>%USERPROFILE%</c> 这类环境变量。
    /// </summary>
    public static string? ReadSavedPath()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                return Environment.ExpandEnvironmentVariables(text).Trim('"');
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取 {ConfigFileName} 失败：{ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 把用户选的目录写进 userdata.txt。写不进去（例如 exe 放在 Program Files）返回 false，
    /// 调用方要明确告诉用户「这次生效但下次还得重选」。
    /// </summary>
    public static bool SavePath(string directory)
    {
        try
        {
            var text = string.Join(Environment.NewLine, new[]
            {
                "# 音乐播放器的数据目录（设置面板 / 首次启动选择时写入）",
                "# 删掉这个文件就回到默认位置 " + DefaultPath,
                directory.Trim(),
                string.Empty,
            });

            File.WriteAllText(ConfigPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"写入 {ConfigFileName} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>展开环境变量，把相对路径补成 exe 目录下的绝对路径</summary>
    public static string Normalize(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        var absolute = Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);

        return Path.GetFullPath(absolute);
    }

    /// <summary>建目录并做一次写测试，失败时给出带路径的清晰错误（比 CEF 内部的报错好懂）</summary>
    public static string Prepare(string path)
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

        var probe = Path.Combine(path, ".write-probe");
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

    /// <summary>不抛异常的 <see cref="Prepare"/>；失败时 <paramref name="message"/> 是给用户看的整句说明</summary>
    public static bool TryPrepare(string path, out string message)
    {
        try
        {
            Prepare(path);
            message = string.Empty;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
