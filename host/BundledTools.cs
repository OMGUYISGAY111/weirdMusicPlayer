using System;
using System.Collections.Generic;
using System.IO;

namespace MusicPlayer.Host;

/// <summary>
/// 查找随包分发的命令行工具（ffmpeg / yt-dlp）。
///
/// 便携包把二进制放在 exe 同目录或 exe 同目录的 bin\ 下，目标机器不需要安装、
/// 也不需要配置 PATH。查找顺序：
///   环境变量（MUSICPLAYER_FFMPEG / MUSICPLAYER_YTDLP）> exe 同目录 > exe\bin > PATH > 安装位置。
/// </summary>
internal static class BundledTools
{
    /// <summary>exe 同目录与 bin 子目录，按优先级排列</summary>
    public static IEnumerable<string> LocalPaths(string exeName)
    {
        var root = AppContext.BaseDirectory;
        yield return Path.Combine(root, exeName);
        yield return Path.Combine(root, "bin", exeName);
    }

    /// <summary>随包工具存在时返回完整路径，否则 null</summary>
    public static string? FindLocal(string exeName)
    {
        foreach (var candidate in LocalPaths(exeName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>PATH 中查找（大小写不敏感的文件名比较由 Windows 文件系统负责）</summary>
    public static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径片段，跳过
            }
        }

        return null;
    }
}
