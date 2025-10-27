using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BitmapIndexSet"/> — column-keyed lookup of bitmap indexes.
/// </summary>
public sealed class BitmapIndexSetTests : ServiceTestBase
{
    [Fact]
    public void TryGetIndex_ExistingColumn_ReturnsTrue()
    {
        BitmapIndexSet set = BuildSet("color");

        bool found = set.TryGetIndex("color", out BitmapColumnIndex? index);

        Assert.True(found);
        Assert.NotNull(index);
    }

    [Fact]
    public void TryGetIndex_MissingColumn_ReturnsFalse()
    {
        BitmapIndexSet set = BuildSet("color");

        bool found = set.TryGetIndex("size", out BitmapColumnIndex? index);

        Assert.False(found);
        Assert.Null(index);
    }

    [Fact]
    public void TryGetIndex_CaseInsensitive()
    {
        BitmapIndexSet set = BuildSet("Color");

        bool found = set.TryGetIndex("COLOR", out BitmapColumnIndex? index);

        Assert.True(found);
        Assert.NotNull(index);
    }

    [Fact]
    public void ColumnNames_ReturnsAllColumns()
    {
        Dictionary<string, BitmapColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = BuildColumnIndex(),
            ["size"] = BuildColumnIndex(),
        };

        BitmapIndexSet set = new(indexes);

        Assert.Equal(2, set.Count);
        Assert.Contains("color", set.ColumnNames);
        Assert.Contains("size", set.ColumnNames);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static BitmapIndexSet BuildSet(string columnName)
    {
        Dictionary<string, BitmapColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = BuildColumnIndex(),
        };

        return new BitmapIndexSet(indexes);
    }

    private static BitmapColumnIndex BuildColumnIndex()
    {
        int rowCount = 8;
        int[] chunkRowCounts = [rowCount];

        ChunkBitmap bitmap = ChunkBitmap.Create(rowCount);
        bitmap.SetBit(0);

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [DataValue.FromString("test")] = [bitmap.Compress()],
        };

        return new BitmapColumnIndex(compressedBitmaps, 1, chunkRowCounts);
    }
}
