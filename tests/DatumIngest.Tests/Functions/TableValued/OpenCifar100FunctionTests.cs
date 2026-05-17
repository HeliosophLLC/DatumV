using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization.Cifar;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_cifar100(bytes)</c> table-valued function: parses a CIFAR-100
/// binary file (1-byte coarse label + 1-byte fine label + 3072-byte planar
/// RGB per record) and yields one row per item.
/// </summary>
public sealed class OpenCifar100FunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_DeclaresIdxImageCoarseFineSchema()
    {
        OpenCifar100Function fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.UInt8]);

        Assert.Equal(4, schema.Columns.Count);
        Assert.Equal("idx", schema.Columns[0].Name);
        Assert.Equal("image", schema.Columns[1].Name);
        Assert.Equal("coarse_label", schema.Columns[2].Name);
        Assert.Equal("fine_label", schema.Columns[3].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[2].Kind);
        Assert.Equal(DataKind.UInt8, schema.Columns[3].Kind);
    }

    [Fact]
    public async Task Open_OnSyntheticBatch_YieldsCoarseAndFineLabelsPerRow()
    {
        byte[] payload = BuildCifar100Batch(coarseFinePairs: [(2, 47), (15, 99)]);

        OpenCifar100Function fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true)], ctx),
            ctx);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0L, rows[0]["idx"].AsInt64());
        Assert.Equal((byte)2, rows[0]["coarse_label"].AsUInt8());
        Assert.Equal((byte)47, rows[0]["fine_label"].AsUInt8());
        Assert.Equal(DataKind.Image, rows[0]["image"].Kind);

        Assert.Equal((byte)15, rows[1]["coarse_label"].AsUInt8());
        Assert.Equal((byte)99, rows[1]["fine_label"].AsUInt8());
    }

    [Fact]
    public async Task Open_OnMisalignedPayload_ThrowsHelpfulError()
    {
        byte[] payload = new byte[CifarRecordReader.RecordSize(labelBytes: 2) + 1];

        OpenCifar100Function fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                .ExecuteAsync([ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true)], ctx))
            { }
        });

        Assert.Contains("open_cifar100", ex.Message);
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

    private static byte[] BuildCifar100Batch((byte coarse, byte fine)[] coarseFinePairs)
    {
        int recordSize = CifarRecordReader.RecordSize(labelBytes: 2);
        byte[] payload = new byte[coarseFinePairs.Length * recordSize];
        for (int i = 0; i < coarseFinePairs.Length; i++)
        {
            int baseOffset = i * recordSize;
            payload[baseOffset]     = coarseFinePairs[i].coarse;
            payload[baseOffset + 1] = coarseFinePairs[i].fine;
            for (int p = 0; p < CifarRecordReader.ImagePixelBytes; p++)
            {
                payload[baseOffset + 2 + p] = (byte)((coarseFinePairs[i].fine * 11 + p) & 0xFF);
            }
        }
        return payload;
    }
}
