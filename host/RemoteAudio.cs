namespace MusicPlayer.Host;

/// <summary>只描述远端资源，不创建或保存音频文件。</summary>
internal sealed record RemoteAudioTrack(string Id, string Title, string Artist, double Duration,
    string Url, string MimeType, Dictionary<string, string> Headers, string? Proxy)
{
    public System.Net.CookieContainer Cookies { get; init; } = new();
    public string SourceKind { get; init; } = "";
    public string OriginalUrl { get; init; } = "";
}

internal sealed class AudioStreamException(string message, bool recoverable) : IOException(message)
{
    public bool Recoverable { get; } = recoverable;
}

