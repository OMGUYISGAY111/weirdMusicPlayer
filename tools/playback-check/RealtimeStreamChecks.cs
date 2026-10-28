using System.Diagnostics;
using System.Reflection;
using System.Buffers.Binary;

internal static class RealtimeStreamChecks
{
    public static void ProbeAudio(Assembly assembly, object track)
    {
        var wave = assembly.GetType("MusicPlayer.Host.VirtualWaveStream", true)!;
        var ffmpeg = assembly.GetType("MusicPlayer.Host.FfmpegLocator", true)!.GetMethod("Find")!.Invoke(null, null)!;
        using var stream = (Stream)Activator.CreateInstance(wave, track, ffmpeg)!;
        var header = new byte[44]; stream.ReadExactly(header);
        var samples = new byte[48000 * 4 * 3]; stream.ReadExactly(samples);
        var energy = 0d; var peak = 0;
        for (var index = 0; index < samples.Length; index += 2)
        {
            var sample = (int)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(index));
            peak = Math.Max(peak, Math.Abs(sample)); energy += (double)sample * sample;
        }
        var rms = Math.Sqrt(energy / (samples.Length / 2));
        Console.WriteLine($"PCM probe: peak={peak}, rms={rms:0.00}");
        if (peak == 0) throw new Exception("前 3 秒转码输出全部为零；进度推进不代表存在真实音频");
    }

    public static string Run(Assembly assembly, string input)
    {
        Type Host(string name) => assembly.GetType("MusicPlayer.Host." + name, true)!;
        var waveType = Host("VirtualWaveStream");
        var ffmpeg = (string?)Host("FfmpegLocator").GetMethod("Find")!.Invoke(null, null)
            ?? throw new Exception("需要 ffmpeg");
        var track = Activator.CreateInstance(Host("RemoteAudioTrack"), new object?[]
        {
            "fixture", "Streaming fixture", "", 24d, input, "audio/wav", new Dictionary<string, string>(), null,
        })!;
        Stream Create() => (Stream)Activator.CreateInstance(waveType, track, ffmpeg)!;
        object WithDuration(double duration, string? url = null) => Activator.CreateInstance(Host("RemoteAudioTrack"), new object?[]
        {
            "fixture", "Incomplete fixture", "", duration, url ?? input, "audio/wav", new Dictionary<string, string>(), null,
        })!;
        foreach (var invalid in new[] { WithDuration(48), WithDuration(24, input + ".missing") })
        {
            using var source = (Stream)Activator.CreateInstance(waveType, invalid, ffmpeg)!;
            var rejected = false;
            try { source.CopyTo(Stream.Null); }
            catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("断流或解码失败不能补静音伪装为播放成功");
        }
        // 小幅容器时长误差可以在正常解码后补齐，但最多 250ms。
        using (var aligned = (Stream)Activator.CreateInstance(waveType, WithDuration(24.05), ffmpeg)!) aligned.CopyTo(Stream.Null);
        Console.WriteLine("PASS: 断流/加载失败明确报错，正常末尾小幅时长误差受限补齐");
        byte[] expected;
        using (var source = Create())
        {
            var header = new byte[44]; source.ReadExactly(header);
            if (waveType.GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source) != null)
                throw new Exception("只读头部不应启动转码进程");
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)) != 24 * 48000 * 4)
                throw new Exception("虚拟 WAV 长度错误");
            source.Position = 0;
            using var output = new MemoryStream(); source.CopyTo(output); expected = output.ToArray();
            foreach (var position in new[] { 44 + 192000 * 11, 44 + 192000 * 2 + 3, expected.Length - 101, 0 })
            {
                source.Position = position;
                var sample = new byte[Math.Min(8192, expected.Length - position)]; source.ReadExactly(sample);
                if (!sample.AsSpan().SequenceEqual(expected.AsSpan(position, sample.Length)))
                    throw new Exception($"实时转码跳转偏移 {position} 的音频与顺序解码不一致");
            }
        }
        using (var source = Create())
        {
            source.Position = 44;
            source.ReadExactly(new byte[4096]);
            var process = (Process)waveType.GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
            var pid = process.Id;
            source.Dispose();
            try
            {
                using var remaining = Process.GetProcessById(pid);
                if (!remaining.WaitForExit(3000)) throw new Exception("取消播放后 ffmpeg 未退出");
            }
            catch (ArgumentException) { }
        }
        var url = (string)Host("StreamRegistry").GetMethod("RegisterTranscoded")!.Invoke(null, new[] { track })!;
        StreamProxyChecks.RunExpected(assembly, url, expected);
        Console.WriteLine("PASS: 按需实时转码、前后跳转、非采样边界 Range 及进程释放（不创建转码文件）");
        return url;
    }
}
