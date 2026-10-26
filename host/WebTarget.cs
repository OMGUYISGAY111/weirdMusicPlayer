using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace MusicPlayer.Host;

/// <summary>
/// 决定 CEF 打开哪个地址：
///   1. --url 显式指定
///   2. --dev 且 Vite dev server 在跑 -> http://127.0.0.1:5173/
///   3. 本地前端产物 -> app://web/index.html（自定义标准协议，走 FolderSchemeHandlerFactory）
/// </summary>
internal sealed class WebTarget
{
    public const string SchemeName = "app";
    public const string DomainName = "web";
    public const string DevServerUrl = "http://127.0.0.1:5173/";

    private WebTarget(string url, bool isDevServer, string? rootDirectory)
    {
        Url = url;
        IsDevServer = isDevServer;
        RootDirectory = rootDirectory;
    }

    public string Url { get; }

    public bool IsDevServer { get; }

    /// <summary>非 null 表示使用 app:// 协议，该目录作为站点根目录</summary>
    public string? RootDirectory { get; }

    public bool UsesAppScheme => RootDirectory is not null;

    public static WebTarget Resolve(StartupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Url))
        {
            return new WebTarget(options.Url!, isDevServer: false, rootDirectory: null);
        }

        if (options.Dev && IsDevServerRunning())
        {
            return new WebTarget(DevServerUrl, isDevServer: true, rootDirectory: null);
        }

        var root = FindWebRoot(options.WebRoot);
        if (root is null)
        {
            const string hint = "找不到前端产物目录（需要包含 index.html）。\n" +
                                "请先执行 pnpm build:web，或用 --web-root <目录> 指定。";
            throw new DirectoryNotFoundException(hint);
        }

        return new WebTarget($"{SchemeName}://{DomainName}/index.html", isDevServer: false, rootDirectory: root);
    }

    private static bool IsDevServerRunning()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var response = client
                .GetAsync(DevServerUrl, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindWebRoot(string? explicitRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            candidates.Add(Path.GetFullPath(explicitRoot!));
        }
        else
        {
            // 构建时拷到输出目录的 web/
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "web"));

            // dotnet run 场景：从 bin/Debug/net8.0-windows/win-x64 逐级向上找仓库里的 renderer/dist
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "renderer", "dist"));
            }

            // 最后兜底：当前工作目录
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "renderer", "dist"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
