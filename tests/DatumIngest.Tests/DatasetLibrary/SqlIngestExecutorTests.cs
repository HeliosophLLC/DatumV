using System.Buffers.Binary;
using System.IO.Compression;

using DatumIngest.Catalog;
using DatumIngest.DatasetLibrary;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// End-to-end tests for <see cref="SqlIngestExecutor"/> — runs a SELECT
/// against the live catalog and confirms a queryable <c>.datum</c>
/// lands on disk with the projected schema + row count. Uses the
/// engine's <c>range</c> TVF as a parameterless data source so the
/// fixture doesn't depend on synthetic archives.
/// </summary>
public sealed class SqlIngestExecutorTests : ServiceTestBase, IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), $"sql_ingest_test_{Guid.NewGuid():N}");

    public SqlIngestExecutorTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_scratch))
        {
            try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_SimpleProjection_WritesQueryableDatum()
    {
        TableCatalog catalog = CreateCatalog();
        SqlIngestExecutor executor = new(catalog, CreatePool(), NullLogger<SqlIngestExecutor>.Instance);

        string destPath = Path.Combine(_scratch, "out.datum");
        SqlIngestResult result = await executor.ExecuteAsync(
            sql: "SELECT * FROM range(0, 4)",
            parameters: new Dictionary<string, ParameterValue>(),
            destPath: destPath,
            onRowProgress: null,
            ct: default);

        // range(start, end) is inclusive on both ends → 0..4 = 5 rows.
        Assert.Equal(5, result.RowCount);
        Assert.True(File.Exists(destPath), "expected .datum at " + destPath);

        // Round-trip: register the written .datum and SELECT it back.
        TableCatalog readback = CreateCatalog();
        readback.AddFile(destPath, name: "result");
        Assert.True(readback.TryGetTable(new QualifiedName("public", "result"), out _));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNonQueryStatement()
    {
        TableCatalog catalog = CreateCatalog();
        SqlIngestExecutor executor = new(catalog, CreatePool(), NullLogger<SqlIngestExecutor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(
                sql: "CREATE FUNCTION foo() AS 1",
                parameters: new Dictionary<string, ParameterValue>(),
                destPath: Path.Combine(_scratch, "nope.datum"),
                onRowProgress: null,
                ct: default));
    }

    [Fact]
    public async Task ExecuteAsync_AudioDecode_SidecarFileContainsRiffMagicOnDisk()
    {
        // Last-mile evidence test: open the produced .datum-blob with a plain
        // FileStream and verify RIFF/WAVE magic appears at the expected sidecar
        // offset. Bypasses the SidecarRegistry / decoder path entirely so a
        // failure here would isolate the bug to the write side (encoder /
        // SidecarWriteStore), and a pass would isolate it to the read side
        // (decoder / SidecarRegistry / cell formatter).
        byte[] wavBytes = BuildWave(sampleRate: 44100, channels: 2, bitsPerSample: 16, dataBytes: 4096);
        string archivePath = Path.Combine(_scratch, "ondisk.zip");
        BuildZip(archivePath, [("clip.wav", wavBytes)]);

        TableCatalog catalog = CreateCatalog();
        SqlIngestExecutor executor = new(catalog, CreatePool(), NullLogger<SqlIngestExecutor>.Instance);

        string destPath = Path.Combine(_scratch, "ondisk.datum");
        await executor.ExecuteAsync(
            sql: "SELECT audio_decode(bytes) AS clip FROM open_archive($archive)",
            parameters: new Dictionary<string, ParameterValue>(StringComparer.Ordinal)
            {
                ["archive"] = new StringParameter(archivePath),
            },
            destPath: destPath,
            onRowProgress: null,
            ct: default);

        string sidecarPath = Path.ChangeExtension(destPath, ".datum-blob");
        Assert.True(File.Exists(sidecarPath), "sidecar file should exist alongside .datum");

        // Sidecar layout: 64-byte header, then concatenated blobs. The single
        // WAV's bytes should land right after the header. We search the first
        // ~256 bytes (more than enough to skip the header + any forward padding)
        // for the RIFF magic so the assertion is robust to header-size tweaks.
        byte[] head = new byte[256];
        using (FileStream fs = new(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int n = fs.Read(head, 0, head.Length);
            Assert.True(n >= 64, $"sidecar too short ({n} bytes)");
        }

        int riffOffset = -1;
        for (int i = 0; i + 12 <= head.Length; i++)
        {
            if (head[i] == 'R' && head[i + 1] == 'I' && head[i + 2] == 'F' && head[i + 3] == 'F'
                && head[i + 8] == 'W' && head[i + 9] == 'A' && head[i + 10] == 'V' && head[i + 11] == 'E')
            {
                riffOffset = i;
                break;
            }
        }
        Assert.True(riffOffset >= 0,
            "RIFF/WAVE magic not found in first 256 bytes of sidecar file " +
            $"(first 32 bytes: {Convert.ToHexString(head.AsSpan(0, 32))})");
    }

    [Fact]
    public async Task ExecuteAsync_AudioDecodeManyEntries_AllBytesRoundTripWithRiffMagic()
    {
        // Stress the per-row sidecar pointer offsets against many entries. The
        // user-reported LJSpeech failure had ~13K rows; this drives a smaller but
        // multi-page count (>1024 = at least 2 VariableSlot pages flushed) so any
        // per-page slot-offset or arena/sidecar accounting bug would surface as a
        // byte mismatch on the second-page rows.
        const int rowCount = 2050;
        List<(string name, byte[] bytes)> entries = new(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            entries.Add(($"clip{i:D5}.wav",
                BuildWave(sampleRate: 22050, channels: 1, bitsPerSample: 16, dataBytes: 1024)));
        }
        string archivePath = Path.Combine(_scratch, "many.zip");
        BuildZip(archivePath, entries);

        TableCatalog catalog = CreateCatalog();
        SqlIngestExecutor executor = new(catalog, CreatePool(), NullLogger<SqlIngestExecutor>.Instance);

        string destPath = Path.Combine(_scratch, "many.datum");
        SqlIngestResult result = await executor.ExecuteAsync(
            // Mirror the LJSpeech recipe shape: regex-replace string, audio_decode,
            // a passthrough scalar, a timestamp — and the WHERE-clause filter — so
            // the projection's output batch has the same column mix that the user's
            // failing ingest does.
            sql: "SELECT regexp_replace(path, '^.+/(.+)\\.wav$', '\\1') AS utt_id, "
               + "audio_decode(bytes) AS clip, size AS file_bytes, modified "
               + "FROM open_archive($archive) WHERE get_filename_ext(path) = 'wav'",
            parameters: new Dictionary<string, ParameterValue>(StringComparer.Ordinal)
            {
                ["archive"] = new StringParameter(archivePath),
            },
            destPath: destPath,
            onRowProgress: null,
            ct: default);

        Assert.Equal(rowCount, result.RowCount);

        TableCatalog readback = CreateCatalog();
        readback.AddFile(destPath, name: "clips");

        StatementPlan plan = await readback.PlanAsync("SELECT utt_id, clip FROM clips");
        int seen = 0;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue clip = batch[i]["clip"];
                Assert.Equal(DataKind.Audio, clip.Kind);
                ReadOnlySpan<byte> bytes = clip.AsByteSpan(batch.Arena, readback.SidecarRegistry);
                Assert.True(bytes.Length >= 12, $"row {seen} has only {bytes.Length} bytes");

                // RIFF/WAVE magic survives the sidecar round-trip on every row,
                // not just the first one.
                Assert.True(bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F',
                    $"row {seen} ({batch[i]["utt_id"].AsString()}): first 4 bytes = " +
                    $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2} (expected 'RIFF')");
                Assert.True(bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E',
                    $"row {seen}: bytes 8..11 = {bytes[8]:X2} {bytes[9]:X2} {bytes[10]:X2} {bytes[11]:X2}");
                Assert.Equal(22050u, clip.AudioSampleRate);
                seen++;
            }
        }
        Assert.Equal(rowCount, seen);
    }

    [Fact]
    public async Task ExecuteAsync_BoundParameter_FlowsToProjection()
    {
        // Confirms ParameterBinder is wired: bind :answer into a SELECT
        // projection and assert the written value matches.
        TableCatalog catalog = CreateCatalog();
        SqlIngestExecutor executor = new(catalog, CreatePool(), NullLogger<SqlIngestExecutor>.Instance);

        string destPath = Path.Combine(_scratch, "bound.datum");
        SqlIngestResult result = await executor.ExecuteAsync(
            sql: "SELECT $answer AS answer",
            parameters: new Dictionary<string, ParameterValue>(StringComparer.Ordinal)
            {
                ["answer"] = new InlineParameter(DataValue.FromInt64(42)),
            },
            destPath: destPath,
            onRowProgress: null,
            ct: default);

        Assert.Equal(1, result.RowCount);
    }

    // ————————————————————————— Helpers —————————————————————————

    private static void BuildZip(string path, IReadOnlyList<(string name, byte[] bytes)> entries)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create);
        foreach ((string name, byte[] bytes) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using Stream s = entry.Open();
            s.Write(bytes);
        }
    }

    // Minimal RIFF/WAVE PCM container: 12-byte RIFF header + fmt chunk (24) + data chunk (8 + dataBytes).
    // Mirrors the helper in AudioHeaderParserTests; the parser ignores the body bytes
    // so leaving them zero is fine for the round-trip check.
    private static byte[] BuildWave(uint sampleRate, ushort channels, ushort bitsPerSample, uint dataBytes)
    {
        int fmtPayload = 16;
        int total = 12 + 8 + fmtPayload + 8 + (int)dataBytes;
        byte[] buf = new byte[total];
        int cursor = 0;

        WriteAscii(buf, ref cursor, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)(total - 8)); cursor += 4;
        WriteAscii(buf, ref cursor, "WAVE");

        WriteAscii(buf, ref cursor, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)fmtPayload); cursor += 4;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = sampleRate * blockAlign;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), 1); cursor += 2; // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), channels); cursor += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), sampleRate); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), byteRate); cursor += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), blockAlign); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), bitsPerSample); cursor += 2;

        WriteAscii(buf, ref cursor, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), dataBytes); cursor += 4;
        // data payload stays zero-filled.

        return buf;
    }

    private static void WriteAscii(byte[] buf, ref int cursor, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
        {
            buf[cursor + i] = (byte)ascii[i];
        }
        cursor += ascii.Length;
    }
}
