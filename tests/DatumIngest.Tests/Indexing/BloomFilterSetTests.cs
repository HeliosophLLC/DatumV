using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Indexing.Bloom;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BloomFilterSet"/> — collection of bloom filters
/// keyed by column name and chunk index.
/// </summary>
public sealed class BloomFilterSetTests
{
    private static readonly Arena Store = new();

    [Fact]
    public void TryGetFilter_ExistingColumnAndChunk_ReturnsTrue()
    {
        BloomFilter filter = new(expectedElements: 100);
        filter.Add(DataValue.FromString("test", Store), Store);

        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = [filter]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.True(set.TryGetFilter("name", 0, out BloomFilter? result));
        Assert.NotNull(result);
        Assert.True(result.MayContain(DataValue.FromString("test", Store), Store));
    }

    [Fact]
    public void TryGetFilter_CaseInsensitive()
    {
        BloomFilter filter = new(expectedElements: 100);

        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = [filter]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.True(set.TryGetFilter("name", 0, out _));
        Assert.True(set.TryGetFilter("NAME", 0, out _));
    }

    [Fact]
    public void TryGetFilter_MissingColumn_ReturnsFalse()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = [new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.False(set.TryGetFilter("nonexistent", 0, out _));
    }

    [Fact]
    public void TryGetFilter_ChunkIndexOutOfRange_ReturnsFalse()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = [new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.False(set.TryGetFilter("name", 1, out _));
        Assert.False(set.TryGetFilter("name", -1, out _));
    }

    [Fact]
    public void HasColumn_ExistingColumn_ReturnsTrue()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["category"] = [new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.True(set.HasColumn("category"));
        Assert.True(set.HasColumn("CATEGORY"));
    }

    [Fact]
    public void HasColumn_MissingColumn_ReturnsFalse()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["category"] = [new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.False(set.HasColumn("other"));
    }

    [Fact]
    public void ColumnNames_ReturnsAllColumns()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = [new BloomFilter(100)],
            ["b"] = [new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 1);

        Assert.Equal(2, set.ColumnNames.Count);
        Assert.Contains("a", set.ColumnNames);
        Assert.Contains("b", set.ColumnNames);
    }

    [Fact]
    public void ChunkCount_ReturnsStoredValue()
    {
        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = [new BloomFilter(100), new BloomFilter(100), new BloomFilter(100)]
        };

        BloomFilterSet set = new(filters, chunkCount: 3);

        Assert.Equal(3, set.ChunkCount);
    }

    [Fact]
    public void MultipleChunks_EachChunkHasIndependentFilter()
    {
        BloomFilter filter0 = new(expectedElements: 100);
        filter0.Add(DataValue.FromString("alpha", Store), Store);

        BloomFilter filter1 = new(expectedElements: 100);
        filter1.Add(DataValue.FromString("beta", Store), Store);

        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = [filter0, filter1]
        };

        BloomFilterSet set = new(filters, chunkCount: 2);

        Assert.True(set.TryGetFilter("name", 0, out BloomFilter? chunk0Filter));
        Assert.True(chunk0Filter!.MayContain(DataValue.FromString("alpha", Store), Store));
        Assert.False(chunk0Filter.MayContain(DataValue.FromString("beta", Store), Store));

        Assert.True(set.TryGetFilter("name", 1, out BloomFilter? chunk1Filter));
        Assert.True(chunk1Filter!.MayContain(DataValue.FromString("beta", Store), Store));
        Assert.False(chunk1Filter.MayContain(DataValue.FromString("alpha", Store), Store));
    }
}
