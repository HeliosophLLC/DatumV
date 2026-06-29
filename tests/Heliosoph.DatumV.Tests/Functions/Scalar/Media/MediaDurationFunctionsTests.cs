using System.Buffers.Binary;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Functions.Video;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Media;

/// <summary>
/// Tests <c>audio_duration()</c> / <c>video_duration()</c> — the elided
/// inline-metadata accessors that derive clip length (seconds, Float64) from the
/// stamped frame count + rate, with a decode-free container-duration fallback.
/// Covers plan-shape elision, end-to-end stamped-path parity, NULL propagation,
/// and the standalone slow-path duration readers.
/// </summary>
public sealed class MediaDurationFunctionsTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    private static string SpikeVideoPath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "spike.mp4");

    /// <summary>
    /// Builds a minimal PCM WAV (mono so the frame count equals the per-channel
    /// sample count, making <c>frame_count ÷ sample_rate</c> an exact duration).
    /// The data payload stays zero-filled — only the header drives metadata.
    /// </summary>
    private static byte[] BuildMonoWave(uint sampleRate, uint frameCount, ushort bitsPerSample = 16)
    {
        const ushort channels = 1;
        uint dataBytes = frameCount * channels * (bitsPerSample / 8u);
        int fmtPayload = 16;
        int total = 12 + 8 + fmtPayload + 8 + (int)dataBytes;
        byte[] buf = new byte[total];
        int cursor = 0;

        void Ascii(string s) { foreach (char c in s) buf[cursor++] = (byte)c; }

        Ascii("RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)(total - 8)); cursor += 4;
        Ascii("WAVE");

        Ascii("fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)fmtPayload); cursor += 4;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = sampleRate * blockAlign;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), 1); cursor += 2; // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), channels); cursor += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), sampleRate); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), byteRate); cursor += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), blockAlign); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), bitsPerSample); cursor += 2;

        Ascii("data");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), dataBytes); cursor += 4;
        return buf;
    }

    private TableCatalog CreateAudioCatalog(byte[]? audioBytes)
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["clip"],
            columnKinds: [DataKind.Audio],
            rows: [[audioBytes]]));
        return catalog;
    }

    private TableCatalog CreateVideoCatalog(byte[]? videoBytes)
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["clip"],
            columnKinds: [DataKind.Video],
            rows: [[videoBytes]]));
        return catalog;
    }

    // ─────────────── Plan-shape elision ───────────────

    [Fact]
    public void AudioDurationCall_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateAudioCatalog(BuildMonoWave(16000, 32000));
        QueryOperator plan = PlanQuery("SELECT audio_duration(clip) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.AudioDuration, elided.Field);
        Assert.IsType<ColumnReference>(elided.Argument);
    }

    [Fact]
    public void VideoDurationCall_RewritesToInlineAccessorExpression()
    {
        TableCatalog catalog = CreateVideoCatalog(System.IO.File.ReadAllBytes(SpikeVideoPath()));
        QueryOperator plan = PlanQuery("SELECT video_duration(clip) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        InlineAccessorExpression elided = Assert.IsType<InlineAccessorExpression>(
            project.Columns[0].Expression);
        Assert.Equal(InlineAccessorField.VideoDuration, elided.Field);
    }

    // ─────────────── End-to-end execution parity ───────────────

    [Fact]
    public async Task AudioDuration_StampedWav_ReturnsExactSeconds()
    {
        // 32000 frames @ 16 kHz mono = exactly 2.0 seconds, served inline.
        TableCatalog catalog = CreateAudioCatalog(BuildMonoWave(16000, 32000));
        List<Row> rows = await ExecuteQueryAsync("SELECT audio_duration(clip) AS d FROM t", catalog);

        Assert.Single(rows);
        Assert.Equal(DataKind.Float64, rows[0]["d"].Kind);
        Assert.Equal(2.0, rows[0]["d"].AsFloat64(), precision: 9);
    }

    [Fact]
    public async Task AudioDuration_NullAudio_ReturnsNullFloat64()
    {
        TableCatalog catalog = CreateAudioCatalog(null);
        List<Row> rows = await ExecuteQueryAsync("SELECT audio_duration(clip) AS d FROM t", catalog);

        Assert.Single(rows);
        Assert.True(rows[0]["d"].IsNull);
        Assert.Equal(DataKind.Float64, rows[0]["d"].Kind);
    }

    [Fact]
    public async Task AudioDuration_FilterAndProject_CollapseAndEvaluate()
    {
        // Same accessor in WHERE + SELECT collapses via CSE on the elided node,
        // and the predicate evaluates against the inline duration.
        TableCatalog catalog = CreateAudioCatalog(BuildMonoWave(44100, 44100)); // 1.0 s
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT audio_duration(clip) AS d FROM t WHERE audio_duration(clip) > 0.5",
            catalog);

        Assert.Single(rows);
        Assert.Equal(1.0, rows[0]["d"].AsFloat64(), precision: 9);
    }

    [Fact]
    public async Task VideoDuration_Spike_ReturnsPositiveSeconds()
    {
        // spike.mp4 is a 72-frame ~30 fps clip → ~2.4 s. The exact value depends
        // on the encoder's frame rate; assert a sane positive bound rather than
        // pinning a brittle constant.
        TableCatalog catalog = CreateVideoCatalog(System.IO.File.ReadAllBytes(SpikeVideoPath()));
        List<Row> rows = await ExecuteQueryAsync("SELECT video_duration(clip) AS d FROM t", catalog);

        Assert.Single(rows);
        Assert.Equal(DataKind.Float64, rows[0]["d"].Kind);
        Assert.False(rows[0]["d"].IsNull);
        double seconds = rows[0]["d"].AsFloat64();
        Assert.InRange(seconds, 1.0, 5.0);
    }

    // ─────────────── Standalone slow-path duration readers ───────────────

    [Fact]
    public void AudioPcmDecoder_TryReadDurationSeconds_Wav_ReturnsSeconds()
    {
        // Exercises the decode-free reader directly (WAV takes the inline fast
        // path in SQL, so this is the only place the reader is hit for WAV).
        byte[] wav = BuildMonoWave(16000, 32000); // 2.0 s
        double? seconds = AudioPcmDecoder.TryReadDurationSeconds(wav);

        Assert.NotNull(seconds);
        Assert.Equal(2.0, seconds!.Value, precision: 2);
    }

    [Fact]
    public void AudioPcmDecoder_TryReadDurationSeconds_Empty_ReturnsNull()
    {
        Assert.Null(AudioPcmDecoder.TryReadDurationSeconds([]));
    }

    [Fact]
    public void VideoHeaderParser_TryReadDurationSeconds_Spike_ReturnsPositive()
    {
        byte[] bytes = System.IO.File.ReadAllBytes(SpikeVideoPath());
        double? seconds = VideoHeaderParser.TryReadDurationSeconds(bytes);

        Assert.NotNull(seconds);
        Assert.InRange(seconds!.Value, 1.0, 5.0);
    }

    [Fact]
    public void VideoHeaderParser_TryReadDurationSeconds_Garbage_ReturnsNull()
    {
        Assert.Null(VideoHeaderParser.TryReadDurationSeconds(new byte[256]));
    }
}
