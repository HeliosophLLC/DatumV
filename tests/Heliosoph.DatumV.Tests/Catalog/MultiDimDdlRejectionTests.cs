using Heliosoph.DatumV.Catalog;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Multi-dim arrays (ndim ≥ 2) currently support fixed-width primitive
/// element kinds and String. The remaining reference / blob kinds and the
/// UInt8 byte-array kind reject at <c>CREATE TABLE</c> so users see the
/// error before any INSERT touches the column.
/// </summary>
public sealed class MultiDimDdlRejectionTests : ServiceTestBase
{
    private InvalidOperationException ExpectRejection(string ddl)
    {
        using TableCatalog catalog = CreateCatalog();
        return Assert.Throws<InvalidOperationException>(() => catalog.Plan(ddl));
    }

    [Fact]
    public void StringMultiDim_AcceptedAtDdl()
    {
        // Slice A: multi-dim Array<String> is supported via
        // FromArenaMultiDimStringArray + a shape-prefix-aware encoder/decoder.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (s Array<String>(2,3))");
    }

    [Fact]
    public void ByteArrayMultiDim_AcceptedAtDdl()
    {
        // Slice B: multi-dim Array<UInt8> is supported. The byte-count path in
        // ElementCount subtracts the shape prefix; AsUInt8Array / AsByteSpan
        // skip the prefix when IsMultiDim is set.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (b Array<UInt8>(2,3))");
    }

    [Fact]
    public void ImageMultiDim_AcceptedAtDdl()
    {
        // Multi-dim Array<Image> is supported via
        // FromArenaMultiDimImageArray + a shape-prefix-aware encoder/decoder
        // path. The original user request (Array<Image>(2,4)) works as of this
        // slice.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (m Array<Image>(2,3))");
    }

    [Fact]
    public void AudioMultiDim_RejectedAtDdl()
    {
        // Audio / Video / Json / PointCloud have a 1-D encoder bug
        // (IsReferenceTypeArray in VariableSlotPageEncoderV2 doesn't list them)
        // — multi-dim support for these kinds is gated on fixing that first.
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (a Array<Audio>(2,3))");
        Assert.Contains("Audio", ex.Message);
    }

    [Fact]
    public void JsonMultiDim_RejectedAtDdl()
    {
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (j Array<Json>(2,3))");
        Assert.Contains("Json", ex.Message);
    }

    [Fact]
    public void MeshMultiDim_RejectedAtDdl()
    {
        // Mesh was missing from the original reject list — multi-dim Mesh
        // would have slipped past DDL and detonated at INSERT. Slice A's
        // cleanup added it to both DDL and runtime guards.
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (m Array<Mesh>(2,3))");
        Assert.Contains("Mesh", ex.Message);
    }

    [Fact]
    public void OneDimStringArray_AcceptedAtDdl()
    {
        // Regression: 1-D Array<String>(N) is still legal — only ndim ≥ 2
        // is rejected for reference kinds.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (s Array<String>(5))");
    }

    [Fact]
    public void OneDimUInt8Array_AcceptedAtDdl()
    {
        // Byte arrays (UInt8 + IsArray) are the canonical blob carrier; the
        // 1-D form must keep working — only multi-dim is rejected.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (b Array<UInt8>(16))");
    }

    [Fact]
    public void VariableLengthStringArray_AcceptedAtDdl()
    {
        // No FixedShape → no multi-dim check ever fires; standard Array<String>.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (s Array<String>)");
    }

    [Fact]
    public void Float32MultiDim_AcceptedAtDdl()
    {
        // Sanity: a supported (fixed-width primitive) multi-dim kind still works.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (m Array<Float32>(2,3))");
    }
}
