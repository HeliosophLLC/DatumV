# VideoDecodeSpike

One-off measurement tool: sequential / downscaled / random-seek decode timings
against an FFMediaToolkit-wrapped libav decoder. The numbers informed the
`VideoFrame` + `VideoRegistry` design; production code lives in
`src/DatumIngest/Model/VideoRegistry.cs` and uses `Sdcb.FFmpeg` instead,
which bundles its own FFmpeg binaries — no setup needed there.

## FFmpeg version

Requires **FFmpeg 7.x shared build** (DLLs, not the static `full_build`).
FFMediaToolkit 4.6.0 binds to the FFmpeg 7.x ABI — 8.x is not ABI-compatible
and will fail to load.

Download the *shared* variant (not `full_build`, not `essentials_build`):

- BtbN autobuilds (keeps historical releases):
  https://github.com/BtbN/FFmpeg-Builds/releases — search for a
  `ffmpeg-n7.1.*-win64-gpl-shared-7.1.zip` (or `-lgpl-shared-7.1.zip`)

## Pointing the spike at the DLLs

Set `FFMPEG_BINARIES` to the directory containing `avcodec-61.dll` /
`avformat-61.dll` / `avutil-59.dll` / `swscale-8.dll` / `swresample-5.dll`:

```powershell
$env:FFMPEG_BINARIES = "C:\path\to\ffmpeg-7.x-shared\bin"
dotnet run --project tools/VideoDecodeSpike --no-build
```

Or extract the shared build under one of the auto-search paths
(`C:\ffmpeg\bin`, `C:\Program Files\ffmpeg\bin`, the Chocolatey/scoop FFmpeg
locations) and the spike will find it without the env var.

## Sample

Defaults to `tests/DatumIngest.Tests/Fixtures/spike.mp4`. Pass any other
H.264 path as `args[0]` to measure that instead.
