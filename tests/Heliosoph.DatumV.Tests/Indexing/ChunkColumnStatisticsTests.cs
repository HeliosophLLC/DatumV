using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Indexing;

public sealed class ChunkColumnStatisticsTests : ServiceTestBase
{
    [Fact]
    public void ToColumnStatisticsRange_MapsAllFields()
    {
        ChunkColumnStatistics stats = new(
            DataValue.FromFloat32(1.0f),
            DataValue.FromFloat32(10.0f),
            NullCount: 3,
            RowCount: 100,
            EstimatedCardinality: 42);

        ColumnStatisticsRange range = stats.ToColumnStatisticsRange();

        Assert.Equal(1.0f, range.Minimum.GetValueOrDefault().AsFloat32());
        Assert.Equal(10.0f, range.Maximum.GetValueOrDefault().AsFloat32());
        Assert.Equal(3, range.NullCount);
        Assert.Equal(100, range.RowCount);
    }

    [Fact]
    public void ToColumnStatisticsRange_NullMinMax_PreservesNulls()
    {
        ChunkColumnStatistics stats = new(
            null, null,
            NullCount: 50,
            RowCount: 50,
            EstimatedCardinality: 0);

        ColumnStatisticsRange range = stats.ToColumnStatisticsRange();

        Assert.Null(range.Minimum);
        Assert.Null(range.Maximum);
    }

    [Fact]
    public void RecordEquality_Works()
    {
        ChunkColumnStatistics a = new(
            DataValue.FromFloat32(1.0f), DataValue.FromFloat32(10.0f),
            NullCount: 0, RowCount: 100, EstimatedCardinality: 50);

        ChunkColumnStatistics b = new(
            DataValue.FromFloat32(1.0f), DataValue.FromFloat32(10.0f),
            NullCount: 0, RowCount: 100, EstimatedCardinality: 50);

        // Records use value equality.
        Assert.Equal(a.NullCount, b.NullCount);
        Assert.Equal(a.RowCount, b.RowCount);
        Assert.Equal(a.EstimatedCardinality, b.EstimatedCardinality);
    }
}
