using System.Text.Json;

namespace MusicPlayer.Host;

internal static class AudioStreamRegistration
{
    public static HashSet<string> SupportedTypes(string? json)
    {
        try { return new HashSet<string>(JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [], StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static (string Url, string Mode) Register(RemoteAudioTrack track, string? supportedTypes)
    {
        if (!string.IsNullOrEmpty(track.MimeType) && SupportedTypes(supportedTypes).Contains(track.MimeType))
            return (StreamRegistry.RegisterRemote(track), "direct");
        if (!double.IsFinite(track.Duration) || track.Duration <= 0)
            throw new InvalidOperationException("当前编码需要实时转码，但没有有效时长；暂不支持该直播或未知时长来源。");
        if (FfmpegLocator.Find() == null)
            throw new InvalidOperationException("当前音频编码需要实时转码，请安装 ffmpeg 或配置 MUSICPLAYER_FFMPEG。");
        if (!string.IsNullOrWhiteSpace(track.Proxy) && Uri.TryCreate(track.Proxy, UriKind.Absolute, out var proxy)
            && proxy.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("实时转码需要 HTTP 代理入口，请把播放器代理配置为 http:// 地址。");
        return (StreamRegistry.RegisterTranscoded(track), "transcode");
    }
}
