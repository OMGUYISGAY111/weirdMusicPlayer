using CefSharp;
using CefSharp.WinForms;
using System.Text.Json;

internal static class RecoveryPlaybackChecks
{
    public static async Task Run(ChromiumWebBrowser browser, string url)
    {
        var harness = Path.Combine(Path.GetTempPath(), "MusicPlayerStreamChecks", "harness", "controller-harness.js");
        if (!File.Exists(harness)) throw new Exception("先运行 node renderer/tests/buildPlaybackHarness.mjs");
        var injected = await browser.EvaluateScriptAsync(File.ReadAllText(harness));
        if (!injected.Success) throw new Exception(injected.Message);
        var result = await browser.EvaluateScriptAsPromiseAsync($$"""
            return (async () => {
              await CefSharp.BindObjectAsync('streamProbe');
              const delay = ms => new Promise(r => setTimeout(r, ms));
              const until = async fn => {
                for (let n = 0; n < 400; n++) { if (fn()) return; await delay(50); }
                throw new Error('恢复状态等待超时');
              };
              const controller = PlaybackHarness.createMediaElementController(0.3, {
                log: (event, data) => { void streamProbe.recordPlaybackLog(JSON.stringify({ event, data })); },
                sourceError: async url => ({ message: await streamProbe.getStreamError(url), recoverable: true }),
                recoverSource: async url => { await delay(1400); return JSON.parse(await streamProbe.renewStream(url)); },
                recoveryDelayMs: 100, errorCheckIntervalMs: 100,
              });
              let ended = 0; controller.on('ended', () => ended++);
              try {
                controller.setMuted(true);
                controller.load({ url: {{JsonSerializer.Serialize(url)}} });
                await until(() => controller.snapshot().duration > 0);
                controller.seek(5);
                await controller.play();
                await until(() => controller.snapshot().currentTime > 5.5);
                await streamProbe.failStream({{JsonSerializer.Serialize(url)}});
                await until(() => controller.snapshot().loading);
                const stopped = controller.snapshot().currentTime;
                await delay(500);
                if (Math.abs(controller.snapshot().currentTime - stopped) > 0.01) throw new Error('缓冲中进度仍走动');
                if (controller.snapshot().error) throw new Error(controller.snapshot().error);
                await until(() => !controller.snapshot().loading && controller.snapshot().currentTime > stopped + 0.7);
                if (ended !== 0) throw new Error('恢复时误触发结束');
                if (controller.snapshot().volume !== 0.3 || !controller.snapshot().muted) throw new Error('音量状态丢失');
                return JSON.stringify({ stopped, resumed: controller.snapshot().currentTime, ended, error: controller.snapshot().error });
              } finally { controller.dispose(); }
            })();
            """, TimeSpan.FromSeconds(50));
        if (!result.Success) throw new Exception(result.Message);
        Console.WriteLine("PASS: 真实 CEF 网络中断原地等待，自动从断点续播 " + result.Result);
    }
}
