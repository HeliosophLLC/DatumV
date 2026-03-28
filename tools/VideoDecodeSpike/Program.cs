using System.Diagnostics;
using FFMediaToolkit;
using FFMediaToolkit.Decoding;
using FFMediaToolkit.Graphics;

// Measures FFmpeg/FFMediaToolkit decode performance to validate the warm-decoder
// design for the VideoFrame DataKind work. Reports:
//   1. Container metadata sanity
//   2. Sequential decode at source resolution (warm-decoder path)
//   3. Sequential decode with swscale downscale to 384px width
//   4. Cold random-access seek latency (cache-miss path)
//
// Usage:
//   video-decode-spike [path-to-video.mp4]
//
// FFmpeg shared libraries (avcodec, avformat, avutil, swscale, swresample) must
// be locatable. The spike searches a few common Windows install locations and
// honors the FFMPEG_BINARIES environment variable.

string videoPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "DatumIngest.Tests", "Fixtures", "spike.mp4"));

if (!File.Exists(videoPath))
{
    Console.Error.WriteLine($"Video not found: {videoPath}");
    Console.Error.WriteLine("Pass a path argument or commit a fixture at tests/DatumIngest.Tests/Fixtures/spike.mp4");
    return 1;
}

string? ffmpegPath = ResolveFFmpegPath();
if (ffmpegPath is not null)
{
    FFmpegLoader.FFmpegPath = ffmpegPath;
}

try
{
    FFmpegLoader.LoadFFmpeg();
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to load FFmpeg shared libraries.");
    Console.Error.WriteLine($"  Tried: {ffmpegPath ?? "(default search path)"}");
    Console.Error.WriteLine($"  Error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Set FFMPEG_BINARIES to a directory containing the FFmpeg 7.x shared-build DLLs");
    Console.Error.WriteLine("(avcodec-61.dll / avformat-61.dll / avutil-59.dll / swscale-8.dll / swresample-5.dll).");
    Console.Error.WriteLine("Pre-built binaries: https://www.gyan.dev/ffmpeg/builds/  (download the 'shared' variant)");
    return 2;
}

Console.WriteLine($"FFmpeg loaded from: {FFmpegLoader.FFmpegPath}");

ReportMetadata(videoPath);
RunSequentialFullRes(videoPath);
RunSequentialDownscaled(videoPath, targetWidth: 384);
RunRandomSeeks(videoPath, seekCount: 30);

return 0;

static string? ResolveFFmpegPath()
{
    string? fromEnv = Environment.GetEnvironmentVariable("FFMPEG_BINARIES");
    if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
    {
        return fromEnv;
    }

    string[] candidates =
    [
        @"C:\ffmpeg\bin",
        @"C:\Program Files\ffmpeg\bin",
        @"C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin",
        @"C:\ProgramData\chocolatey\bin",
    ];

    foreach (string c in candidates)
    {
        if (Directory.Exists(c) && Directory.GetFiles(c, "avcodec-*.dll").Length > 0)
        {
            return c;
        }
    }

    return null;
}

static void ReportMetadata(string path)
{
    using MediaFile file = MediaFile.Open(path);
    VideoStream video = file.Video
        ?? throw new InvalidOperationException("File contains no video stream.");

    Console.WriteLine();
    Console.WriteLine("══════ File metadata ══════");
    Console.WriteLine($"  Path:            {path}");
    Console.WriteLine($"  Resolution:      {video.Info.FrameSize.Width}×{video.Info.FrameSize.Height}");
    Console.WriteLine($"  Codec:           {video.Info.CodecName}");
    Console.WriteLine($"  Pixel format:    {video.Info.PixelFormat}");
    Console.WriteLine($"  Duration:        {video.Info.Duration}");
    Console.WriteLine($"  Avg frame rate:  {video.Info.AvgFrameRate:F3} fps");
    Console.WriteLine($"  Frame count:     {(video.Info.NumberOfFrames?.ToString() ?? "(not stored)")}");
}

static void RunSequentialFullRes(string path)
{
    MediaOptions opts = new() { VideoPixelFormat = ImagePixelFormat.Rgb24 };
    using MediaFile file = MediaFile.Open(path, opts);
    VideoStream video = file.Video!;

    Stopwatch sw = Stopwatch.StartNew();
    int frameCount = 0;
    long bytesDecoded = 0;
    while (video.TryGetNextFrame(out ImageData frame))
    {
        frameCount++;
        bytesDecoded += frame.Data.Length;
    }
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine($"══════ Sequential decode @ {video.Info.FrameSize.Width}×{video.Info.FrameSize.Height} ══════");
    Report(frameCount, sw.Elapsed, bytesDecoded);
}

static void RunSequentialDownscaled(string path, int targetWidth)
{
    // Probe source dimensions to compute aspect-preserving target height.
    int srcWidth, srcHeight;
    using (MediaFile probe = MediaFile.Open(path))
    {
        srcWidth = probe.Video!.Info.FrameSize.Width;
        srcHeight = probe.Video.Info.FrameSize.Height;
    }
    int targetHeight = (int)Math.Round(srcHeight * (double)targetWidth / srcWidth);

    MediaOptions opts = new()
    {
        VideoPixelFormat = ImagePixelFormat.Rgb24,
        TargetVideoSize = new System.Drawing.Size(targetWidth, targetHeight),
    };
    using MediaFile file = MediaFile.Open(path, opts);
    VideoStream video = file.Video!;

    Stopwatch sw = Stopwatch.StartNew();
    int frameCount = 0;
    long bytesDecoded = 0;
    while (video.TryGetNextFrame(out ImageData frame))
    {
        frameCount++;
        bytesDecoded += frame.Data.Length;
    }
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine($"══════ Sequential decode → {targetWidth}×{targetHeight} (swscale resize) ══════");
    Report(frameCount, sw.Elapsed, bytesDecoded);
}

static void RunRandomSeeks(string path, int seekCount)
{
    MediaOptions opts = new() { VideoPixelFormat = ImagePixelFormat.Rgb24 };
    using MediaFile file = MediaFile.Open(path, opts);
    VideoStream video = file.Video!;

    TimeSpan duration = video.Info.Duration;
    if (duration <= TimeSpan.Zero)
    {
        Console.WriteLine();
        Console.WriteLine("══════ Random-access seek ══════");
        Console.WriteLine("  Skipped — container does not expose duration.");
        return;
    }

    Random rng = new(Seed: 42);
    double[] elapsedMs = new double[seekCount];
    int failures = 0;
    Stopwatch sw = new();

    for (int i = 0; i < seekCount; i++)
    {
        double t = rng.NextDouble() * duration.TotalSeconds;
        sw.Restart();
        bool ok = video.TryGetFrame(TimeSpan.FromSeconds(t), out ImageData _);
        sw.Stop();
        elapsedMs[i] = sw.Elapsed.TotalMilliseconds;
        if (!ok) failures++;
    }

    Array.Sort(elapsedMs);
    double mean = elapsedMs.Average();

    Console.WriteLine();
    Console.WriteLine($"══════ Random-access seek ({seekCount} seeks, {failures} failures) ══════");
    Console.WriteLine($"  min:    {elapsedMs[0]:F2} ms");
    Console.WriteLine($"  mean:   {mean:F2} ms");
    Console.WriteLine($"  median: {elapsedMs[seekCount / 2]:F2} ms");
    Console.WriteLine($"  p95:    {elapsedMs[(int)(seekCount * 0.95)]:F2} ms");
    Console.WriteLine($"  max:    {elapsedMs[seekCount - 1]:F2} ms");
}

static void Report(int frameCount, TimeSpan elapsed, long bytesDecoded)
{
    double totalMs = elapsed.TotalMilliseconds;
    double msPerFrame = totalMs / frameCount;
    double fps = frameCount / elapsed.TotalSeconds;
    double mbDecoded = bytesDecoded / (1024.0 * 1024.0);
    double mbps = mbDecoded / elapsed.TotalSeconds;
    Console.WriteLine($"  Frames decoded:  {frameCount}");
    Console.WriteLine($"  Total time:      {totalMs:F1} ms");
    Console.WriteLine($"  Per-frame:       {msPerFrame:F2} ms");
    Console.WriteLine($"  Decode rate:     {fps:F1} fps");
    Console.WriteLine($"  Output volume:   {mbDecoded:F1} MB ({mbps:F1} MB/s)");
}
