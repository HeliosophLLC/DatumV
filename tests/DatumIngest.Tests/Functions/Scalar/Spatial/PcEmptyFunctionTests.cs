using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PcEmptyFunction"/> — the SCAN INIT seed for
/// PointCloud accumulator folds.
/// </summary>
public sealed class PcEmptyFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ReturnsZeroPointCloud()
    {
        ValueRef result = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.PointCloud, result.Kind);
        Assert.False(result.IsNull);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
        Assert.Equal(0u, header.Width);
        Assert.Equal(0u, header.Height);
        Assert.False(header.HasColor);
        Assert.False(header.IsOrganized);
        Assert.Equal(PointCloudCoordinateFrame.Unspecified, header.CoordinateFrame);
    }

    [Fact]
    public async Task BlobIsExactlyTheHeaderSize()
    {
        // Zero points means zero per-point payload — the blob is exactly the
        // 40-byte fixed header. Anything larger would be wasted bytes for
        // every SCAN tick where the accumulator starts empty.
        ValueRef result = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        Assert.Equal(PointCloudHeader.SizeBytes, result.AsPointCloud().Length);
    }

    [Fact]
    public async Task MetadataIsRegistered()
    {
        Assert.Equal("pc_empty", PcEmptyFunction.Name);
        Assert.NotEmpty(PcEmptyFunction.Description);
        Assert.Single(PcEmptyFunction.Signatures);
        Assert.Empty(PcEmptyFunction.Signatures[0].Parameters);
    }
}
