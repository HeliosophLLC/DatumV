using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization.Cifar;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_cifar10(bytes)</c> table-valued function: parses a CIFAR-10
/// binary batch (1-byte label + 3072-byte planar RGB per record) and
/// yields one row per item. Covers the schema contract, the record
/// round-trip, the misaligned-payload error, and the NULL pass-through.
/// </summary>
public sealed class OpenCifar10FunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresIdxImageLabelSchema()
    {
        OpenCifar10Function fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.UInt8]);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("idx", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);

        Assert.Equal("image", schema.Columns[1].Name);
        Assert.Equal(DataKind.Image, schema.Columns[1].Kind);

        Assert.Equal("label", schema.Columns[2].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[2].Kind);
    }

    [Fact]
    public void ValidateArguments_RejectsNonByteArray()
    {
        OpenCifar10Function fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]));
    }

    [Fact]
    public async Task Open_OnSyntheticBatch_YieldsOneRowPerRecordWithLabel()
    {
        byte[] payload = BuildCifar10Batch(labels: [3, 5, 9]);

        OpenCifar10Function fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true)], ctx),
            ctx);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0L, rows[0]["idx"].AsInt64());
        Assert.Equal((byte)3, rows[0]["label"].AsUInt8());
        Assert.Equal(DataKind.Image, rows[0]["image"].Kind);

        Assert.Equal((byte)5, rows[1]["label"].AsUInt8());
        Assert.Equal((byte)9, rows[2]["label"].AsUInt8());
    }

    [Fact]
    public async Task Open_OnMisalignedPayload_ThrowsHelpfulError()
    {
        // 3073 bytes is one CIFAR-10 record plus one stray byte — not a multiple
        // of the record size.
        byte[] payload = new byte[CifarRecordReader.RecordSize(labelBytes: 1) + 1];

        OpenCifar10Function fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                .ExecuteAsync([ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true)], ctx))
            { }
        });

        Assert.Contains("open_cifar10", ex.Message);
        Assert.Contains("CIFAR-10", ex.Message);
    }

    [Fact]
    public async Task Open_OnNullBytes_YieldsNoRows()
    {
        OpenCifar10Function fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.Null(DataKind.UInt8)], ctx), ctx);

        Assert.Empty(rows);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches, ExecutionContext ctx)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, ctx.Store);
                }
                rows.Add(new Row(source.ColumnLookup, stabilized));
            }
        }
        return rows;
    }

    internal static byte[] BuildCifar10Batch(byte[] labels)
    {
        int recordSize = CifarRecordReader.RecordSize(labelBytes: 1);
        byte[] payload = new byte[labels.Length * recordSize];
        for (int i = 0; i < labels.Length; i++)
        {
            int baseOffset = i * recordSize;
            payload[baseOffset] = labels[i];
            for (int p = 0; p < CifarRecordReader.ImagePixelBytes; p++)
            {
                payload[baseOffset + 1 + p] = (byte)((labels[i] * 7 + p * 3) & 0xFF);
            }
        }
        return payload;
    }
}
