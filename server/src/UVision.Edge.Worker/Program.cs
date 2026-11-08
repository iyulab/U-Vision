using System.Collections;
using System.Runtime.InteropServices;

namespace UVision.Edge.Worker;

// The software inference worker: it looks (decode, detect, track) and tells the edge agent what it sees - it never decides.
// Configuration is environment variables only; see WorkerOptions.
//
// A named class, not top-level statements: top-level statements create a global `Program`, which is ambiguous in the test project that
// references both this host and the engine.
internal static class WorkerProgram
{
    internal static async Task<int> Main()
    {
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.Ordinal);

        WorkerOptions options;
        try { options = WorkerOptions.FromEnvironment(env); }
        catch (WorkerConfigException e)
        {
            foreach (var p in e.Problems) Console.Error.WriteLine($"uvision-edge-worker: {p}");
            return 2;
        }

        static void Log(string message) => Console.Out.WriteLine($"{Wire.Iso(DateTimeOffset.UtcNow)} {message}");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => stop.Cancel());

        using var link = new UnixSocketLink(options.SocketPath, TimeProvider.System);
        using var http = HttpDetector.CreateClient(options.DetectorUrl, options.DetectorToken);
        var detector = new HttpDetector(http, options.ScoreThreshold, options.Classes);
        var frames = new FfmpegFrameSourceFactory(options.Ffmpeg, options.SampleFps, options.MaxWidth);

        Log($"uvision-edge-worker {typeof(CameraSupervisor).Assembly.GetName().Version} - socket={options.SocketPath} cameras={options.CamerasFile} detector={options.DetectorUrl.GetLeftPart(UriPartial.Authority)} fps={options.SampleFps} loop={options.Loop}");
        await new CameraSupervisor(options, link, detector, frames, TimeProvider.System, Log).RunAsync(stop.Token);
        Log("stopped");
        return 0;
    }
}
