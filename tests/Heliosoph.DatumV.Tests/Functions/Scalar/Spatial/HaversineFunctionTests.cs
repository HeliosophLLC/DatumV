using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <c>haversine(lat1, lon1, lat2, lon2)</c> — great-circle meters
/// on the spherical earth model. Verified against well-known city pairs,
/// degenerate inputs (same point, antipodes), null propagation, and the
/// filter-shaped usage the function exists for.
/// </summary>
public sealed class HaversineFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_haversine_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private async Task<DataValue> EvaluateAsync(string expression)
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync($"SELECT {expression} AS d", catalog, store: arena);
        return rows[0]["d"];
    }

    [Fact]
    public async Task NewYorkToLosAngeles_MatchesKnownDistance()
    {
        // NYC (40.7128, -74.0060) → LA (34.0522, -118.2437) ≈ 3,936 km.
        DataValue result = await EvaluateAsync(
            "haversine(40.7128, -74.0060, 34.0522, -118.2437)");
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.InRange(result.AsFloat64(), 3_915_000, 3_955_000);
    }

    [Fact]
    public async Task SamePoint_IsZero()
    {
        DataValue result = await EvaluateAsync(
            "haversine(39.9612, -82.9988, 39.9612, -82.9988)");
        Assert.Equal(0.0, result.AsFloat64(), 6);
    }

    [Fact]
    public async Task Antipodes_IsHalfCircumference()
    {
        // (0, 0) → (0, 180): half the great circle, π × 6371008.8 ≈ 20,015,115 m.
        DataValue result = await EvaluateAsync("haversine(0, 0, 0, 180)");
        Assert.InRange(result.AsFloat64(), 20_014_000, 20_016_000);
    }

    [Fact]
    public async Task ShortRange_CityBlocksAreMeters()
    {
        // Two downtown Columbus points ~1.2 km apart; asserts the meter scale
        // (a km/mile mix-up would fail by three orders of magnitude).
        DataValue result = await EvaluateAsync(
            "haversine(39.9612, -82.9988, 39.9690, -83.0007)");
        Assert.InRange(result.AsFloat64(), 700, 1_500);
    }

    [Fact]
    public async Task NullArgument_PropagatesNull()
    {
        DataValue result = await EvaluateAsync(
            "haversine(cast(NULL as Float64), -82.9988, 39.9690, -83.0007)");
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Sql_WithinRadiusFilter()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE places (id Int32, lat Float64, lon Float64)");
        catalog.Plan("INSERT INTO places VALUES "
            + "(1, 39.9612, -82.9988)," // downtown Columbus — inside
            + "(2, 39.9690, -83.0007)," // ~1.2 km away — inside
            + "(3, 40.7128, -74.0060)"); // NYC — outside

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id FROM places WHERE haversine(lat, lon, 39.9612, -82.9988) < 16093.4 ORDER BY id",
            catalog, store: arena);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal(2, rows[1]["id"].AsInt32());
    }
}
