using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="Arena"/> float and byte operations — append, retrieval, materialisation, and copy.
/// </summary>
public class DataArenaTests : ServiceTestBase
{
    [Fact]
    public void AppendAndRetrieveFloats()
    {
        using Arena arena = new();
        float[] values = [1.0f, 2.5f, 3.75f];
        (long offset, int count) = arena.AppendFloats(values);

        ReadOnlySpan<float> span = arena.GetFloats(offset, count);

        Assert.Equal(values, span.ToArray());
    }

    [Fact]
    public void AppendAndRetrieveBytes()
    {
        using Arena arena = new();
        byte[] data = [0xFF, 0x00, 0xAB, 0xCD];
        (long offset, int length) = arena.AppendBytes(data);

        ReadOnlySpan<byte> span = arena.GetBytes(offset, length);

        Assert.Equal(data, span.ToArray());
    }

    [Fact]
    public void MaterializeFloatsCreatesNewArray()
    {
        using Arena arena = new();
        float[] values = [1.0f, 2.0f];
        (long offset, int count) = arena.AppendFloats(values);

        float[] materialized = arena.MaterializeFloats(offset, count);

        Assert.Equal(values, materialized);
        Assert.NotSame(values, materialized);
    }

    [Fact]
    public void MaterializeBytesCreatesNewArray()
    {
        using Arena arena = new();
        byte[] data = [0x01, 0x02, 0x03];
        (long offset, int length) = arena.AppendBytes(data);

        byte[] materialized = arena.MaterializeBytes(offset, length);

        Assert.Equal(data, materialized);
        Assert.NotSame(data, materialized);
    }

    [Fact]
    public void BytesWrittenTracksTotal()
    {
        using Arena arena = new();
        Assert.Equal(0, arena.BytesWritten);

        arena.AppendFloats([1.0f, 2.0f]);
        Assert.Equal(8, arena.BytesWritten);

        arena.AppendBytes([0xFF, 0x00]);
        Assert.Equal(10, arena.BytesWritten);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        using Arena arena = new(initialCapacity: 8);
        float[] large = new float[500];
        (long offset, int count) = arena.AppendFloats(large);

        Assert.Equal(500, count);
        Assert.Equal(large, arena.GetFloats(offset, count).ToArray());
    }

    [Fact]
    public void CopyFromTransfersAllBytes()
    {
        using Arena source = new();
        float[] values = [42.0f, 99.0f];
        (long sourceOffset, int sourceCount) = source.AppendFloats(values);

        using Arena target = new();
        target.AppendBytes([0x01]);
        long baseOffset = target.CopyFrom(source);

        float[] result = target.MaterializeFloats(baseOffset + sourceOffset, sourceCount);
        Assert.Equal(values, result);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        Arena arena = new();
        arena.AppendFloats([1.0f]);
        arena.Dispose();
        arena.Dispose();
    }
}
