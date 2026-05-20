using Heliosoph.DatumV.Catalog;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Multi-dim arrays (ndim ≥ 2) only support fixed-width primitive element
/// kinds — byte / reference / blob kinds collide with the multi-dim
/// metadata packing. The validation fires at <c>CREATE TABLE</c> time so
/// users see the error before any INSERT touches the column.
/// </summary>
public sealed class MultiDimDdlRejectionTests : ServiceTestBase
{
    private InvalidOperationException ExpectRejection(string ddl)
    {
        using TableCatalog catalog = CreateCatalog();
        return Assert.Throws<InvalidOperationException>(() => catalog.Plan(ddl));
    }

    [Fact]
    public void StringMultiDim_RejectedAtDdl()
    {
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (s Array<String>(2,3))");
        Assert.Contains("multi-dim", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public void ByteArrayMultiDim_RejectedAtDdl()
    {
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (b Array<UInt8>(2,3))");
        Assert.Contains("UInt8", ex.Message);
    }

    [Fact]
    public void ImageMultiDim_RejectedAtDdl()
    {
        InvalidOperationException ex = ExpectRejection(
            "CREATE TEMP TABLE t (m Array<Image>(2,3))");
        Assert.Contains("Image", ex.Message);
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
