using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicPlayer.Host;

/// <summary>
/// 用户设置：持久化在数据目录的 settings.json，由界面（首页 / 搜索页的齿轮按钮）修改。
///
/// 为什么不用环境变量当唯一入口：
///   - 界面上改的设置要**立即生效**，环境变量只影响之后启动的进程；
///   - 设置文件可以随时切换 / 清空，删掉文件就回到「未配置」；
///   - 环境变量仍然支持（命令行、CI 里的 tools/* 检查都靠它），只是优先级低于界面设置。
///
/// 本文件不引用 WinForms / PlaybackLog（tools 里的检查项目会直接链接它）。
/// </summary>
internal sealed class PlayerSettings
{
    internal const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 空值不落盘：删掉某个设置就是 JSON 里没有这个字段
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static SettingsData _data = new();
    private static string? _directory;
    private static string? _loadWarning;

    /// <summary>settings.json 的内容；目前只有 YouTube 登录方式。</summary>
    internal sealed class SettingsData
    {
        /// <summary>Netscape 格式 Cookie 文件路径（绝对路径，界面保存时已规范化）</summary>
        // 显式写键名：CamelCase 策略会把 YouTubeCookiesFile 变成 youTubeCookiesFile
        [JsonPropertyName("youtubeCookiesFile")]
        public string? YouTubeCookiesFile { get; set; }

        /// <summary>直接读取已登录浏览器的名称，语法同 yt-dlp --cookies-from-browser</summary>
        [JsonPropertyName("youtubeCookiesBrowser")]
        public string? YouTubeCookiesBrowser { get; set; }

        internal SettingsData Clone() => new()
        {
            YouTubeCookiesFile = YouTubeCookiesFile,
            YouTubeCookiesBrowser = YouTubeCookiesBrowser,
        };
    }

    internal static string? DataDirectory
    {
        get { lock (Gate) { return _directory; } }
    }

    internal static string? FilePath
    {
        get { lock (Gate) { return _directory is null ? null : Path.Combine(_directory, FileName); } }
    }

    /// <summary>设置文件读不出来时的提示（不阻塞启动，交给界面显示）</summary>
    internal static string? LoadWarning
    {
        get { lock (Gate) { return _loadWarning; } }
    }

    /// <summary>数据目录确定后调用一次；文件不存在或内容损坏都按「未配置」处理。</summary>
    internal static void Initialize(string userDataDirectory)
    {
        lock (Gate)
        {
            _directory = userDataDirectory;
            _loadWarning = null;
            _data = new SettingsData();

            var path = _directory is null ? null : Path.Combine(_directory, FileName);
            if (path is null || !File.Exists(path))
            {
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path), JsonOptions);
                if (loaded is null) return;
                _data = new SettingsData
                {
                    YouTubeCookiesFile = Trimmed(loaded.YouTubeCookiesFile),
                    YouTubeCookiesBrowser = Trimmed(loaded.YouTubeCookiesBrowser),
                };
            }
            catch (Exception ex)
            {
                _loadWarning = $"设置文件读不出来（{path}），已按未配置处理：{ex.Message}";
            }
        }
    }

    internal static SettingsData Read()
    {
        lock (Gate) { return _data.Clone(); }
    }

    /// <summary>
    /// 校验并保存，返回保存后的内容。用户输入有问题时抛 <see cref="InvalidOperationException"/>，
    /// 调用方（HostApi）把 message 直接显示在设置面板里。
    /// </summary>
    internal static SettingsData Save(string? cookiesFile, string? cookiesBrowser)
    {
        var file = NormalizeCookiesFile(cookiesFile);
        var browser = NormalizeBrowser(cookiesBrowser);

        lock (Gate)
        {
            if (_directory is null)
            {
                throw new InvalidOperationException("设置文件路径尚未初始化，请重启播放器后重试。");
            }

            var next = new SettingsData
            {
                YouTubeCookiesFile = file,
                YouTubeCookiesBrowser = browser,
            };
            WriteToDisk(next);
            _data = next;
            return next.Clone();
        }
    }

    /// <summary>实际生效的 Cookie 文件：界面设置优先，未设置时回退环境变量。</summary>
    internal static string? EffectiveCookiesFile
    {
        get
        {
            var saved = Read().YouTubeCookiesFile;
            return string.IsNullOrWhiteSpace(saved)
                ? EnvironmentVariable(YtDlpAuthentication.CookieFileVariable)
                : saved;
        }
    }

    /// <summary>实际生效的浏览器；Cookie 文件存在时 yt-dlp 会忽略它。</summary>
    internal static string? EffectiveCookiesBrowser
    {
        get
        {
            var saved = Read().YouTubeCookiesBrowser;
            return string.IsNullOrWhiteSpace(saved)
                ? EnvironmentVariable(YtDlpAuthentication.BrowserVariable)
                : saved;
        }
    }

    /// <summary>生效值的来源，界面上告诉用户「这条配置是从哪来的」。</summary>
    internal static string DescribeSource(bool cookiesFile)
    {
        var saved = cookiesFile ? Read().YouTubeCookiesFile : Read().YouTubeCookiesBrowser;
        if (!string.IsNullOrWhiteSpace(saved)) return "设置";

        var variable = cookiesFile ? YtDlpAuthentication.CookieFileVariable : YtDlpAuthentication.BrowserVariable;
        return EnvironmentVariable(variable) is null ? "" : $"环境变量 {variable}";
    }

    internal static string? EnvironmentCookiesFile => EnvironmentVariable(YtDlpAuthentication.CookieFileVariable);

    internal static string? EnvironmentCookiesBrowser => EnvironmentVariable(YtDlpAuthentication.BrowserVariable);

    private static string? EnvironmentVariable(string name) => Trimmed(Environment.GetEnvironmentVariable(name));

    private static void WriteToDisk(SettingsData data)
    {
        var path = FilePath ?? throw new InvalidOperationException("设置文件路径尚未初始化。");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再覆盖，避免写一半断电留下坏 JSON
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>支持 %USERPROFILE% 这类变量；相对路径按数据目录解析，路径不存在直接报错。</summary>
    private static string? NormalizeCookiesFile(string? raw)
    {
        var text = Trimmed(raw)?.Trim('"', '\'').Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        string expanded;
        string full;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(text);
            var baseDirectory = DataDirectory ?? AppContext.BaseDirectory;
            full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cookie 文件路径无效：{text}（{ex.Message}）");
        }

        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"找不到 Cookie 文件：{full}");
        }

        return full;
    }

    private static string? NormalizeBrowser(string? raw)
    {
        var text = Trimmed(raw);
        if (text is null) return null;
        if (text.Length > 120 || text.IndexOfAny(new[] { '"', '\'', '<', '>' }) >= 0)
        {
            throw new InvalidOperationException("浏览器名称看起来不对，请填 edge、chrome、firefox 这类名称，需要指定配置时写成 edge:Default。");
        }

        return text;
    }

    private static string? Trimmed(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

/// <summary>Cookie 文件体检结果，用来在设置面板上直接说清「这个文件能不能用」。</summary>
internal sealed record CookieFileInfo(
    string FilePath,
    bool Exists,
    long Size,
    DateTime? Modified,
    bool Netscape,
    int CookieCount,
    int ExpiredCount,
    string Note);

internal static class CookieFileInspector
{
    /// <summary>只扫前 2 MB，避免用户误选一个巨大的文件时卡住界面</summary>
    private const int MaxChars = 2 * 1024 * 1024;

    internal static CookieFileInfo Inspect(string? rawPath)
    {
        var path = string.IsNullOrWhiteSpace(rawPath) ? null : rawPath.Trim();
        if (path is null)
        {
            return new CookieFileInfo("", false, 0, null, false, 0, 0, "");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
        }
        catch (Exception ex)
        {
            return new CookieFileInfo(path, false, 0, null, false, 0, 0, $"路径无效：{ex.Message}");
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return new CookieFileInfo(full, false, 0, null, false, 0, 0,
                    "文件不存在，yt-dlp 仍会按默认方式请求，通常会被人机验证拦住。");
            }

            var (netscape, cookies, expired, note) = Scan(full);
            return new CookieFileInfo(full, true, info.Length, info.LastWriteTime, netscape, cookies, expired, note);
        }
        catch (Exception ex)
        {
            return new CookieFileInfo(full, false, 0, null, false, 0, 0, $"读取文件失败：{ex.Message}");
        }
    }

    private static (bool Netscape, int Cookies, int Expired, string Note) Scan(string path)
    {
        var netscape = false;
        var cookies = 0;
        var expired = 0;
        var chars = 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            chars += line.Length + 2;
            if (chars > MaxChars) break;
            if (line.Length == 0) continue;
            if (!netscape && line.Contains("Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase))
            {
                netscape = true;
                continue;
            }

            if (line[0] == '#') continue;

            // 标准 Netscape 行：domain \\t includeSubdomains \\t path \\t secure \\t expiry \\t name \\t value
            var fields = line.Split('\t');
            if (fields.Length < 7) continue;
            netscape = true;

            var domain = fields[0];
            if (!domain.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                && !domain.Contains("google.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cookies += 1;
            if (long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry)
                && expiry > 0 && expiry < now)
            {
                expired += 1;
            }
        }

        if (!netscape)
        {
            return (false, cookies, expired,
                "没有找到 Netscape 格式的 Cookie 记录，yt-dlp 可能读不到登录态；请按官方说明重新导出。");
        }

        if (cookies == 0)
        {
            return (true, 0, 0, "没有找到 youtube.com / google.com 的 Cookie，文件可能是别的站点导出的。");
        }

        if (expired >= cookies)
        {
            return (true, cookies, expired, $"这 {cookies} 条 YouTube Cookie 全部已过期，需要重新导出。");
        }

        return expired > 0
            ? (true, cookies, expired, $"共 {cookies} 条 YouTube Cookie，其中 {expired} 条已过期。")
            : (true, cookies, expired, $"共 {cookies} 条 YouTube Cookie，未发现过期。");
    }
}
