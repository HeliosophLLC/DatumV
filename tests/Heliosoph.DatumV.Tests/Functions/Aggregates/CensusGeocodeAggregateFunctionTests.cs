using System.Net;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Functions.Geocoding;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="CensusGeocodeAggregateFunction"/> —
/// <c>census_geocode_agg(id, street, city, state, zip [, options])</c>. Verifies
/// the Array&lt;Struct&gt; result contract (registry-resolvable element TypeId,
/// id-keyed correlation against an out-of-order response), int and string id
/// handling, NULL / duplicate-id errors, the benchmark option, transport-failure
/// mapping, and the aggregate lifecycle (Merge equivalence, Reset reuse). All
/// geocoder traffic goes through a stubbed <see cref="CensusGeocoderClient"/>
/// swapped in via <see cref="CensusGeocoderClient.Instance"/>; every swap is
/// restored so no other test can observe the stub.
/// </summary>
public sealed class CensusGeocodeAggregateFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_censusgeo_{Guid.NewGuid():N}");
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

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Response covering all verdict shapes for ids 1–3, deliberately out of input
    /// order so tests prove correlation is by id, not position. Id 1 matches in
    /// Columbus OH, id 2 is a No_Match, id 3 is a Non_Exact match.
    /// </summary>
    private const string ThreeRowResponse =
        "\"2\",\"123 NOWHERE ST, SPRINGFIELD, XX, 00000\",\"No_Match\"\r\n"
        + "\"1\",\"100 MAIN ST, COLUMBUS, OH, 43215\",\"Match\",\"Exact\","
        + "\"100 MAIN ST, COLUMBUS, OH, 43215\",\"-83.0007,39.9612\",\"11\",\"L\"\r\n"
        + "\"3\",\"200 HIGH ST, COLUMBUS, OH, 43215\",\"Match\",\"Non_Exact\","
        + "\"200 HIGH ST, COLUMBUS, OH, 43215\",\"-82.9988,39.9690\",\"12\",\"R\"\r\n";

    /// <summary>Handler answering every POST with a canned body (HTTP 200).</summary>
    private sealed class CannedHandler(string responseBody) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    /// <summary>
    /// Swaps <see cref="CensusGeocoderClient.Instance"/> for the test body and
    /// restores the previous client on dispose. Tests in this class run
    /// sequentially (one xUnit collection), so the static swap cannot race.
    /// </summary>
    private sealed class ClientSwap : IDisposable
    {
        private readonly CensusGeocoderClient _previous;
        private readonly CensusGeocoderClient _current;

        public ClientSwap(CensusGeocoderClient replacement)
        {
            _previous = CensusGeocoderClient.Instance;
            _current = replacement;
            CensusGeocoderClient.Instance = replacement;
        }

        public void Dispose()
        {
            CensusGeocoderClient.Instance = _previous;
            _current.Dispose();
        }
    }

    private static ClientSwap UseCannedResponse(string responseBody, out CannedHandler handler)
    {
        handler = new CannedHandler(responseBody);
        return new ClientSwap(new CensusGeocoderClient(handler));
    }

    private (Arena Arena, TypeRegistry Types, InvocationFrame Frame) CreateContext()
    {
        Arena arena = CreateArena();
        TypeRegistry types = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena, null, types);
        return (arena, types, frame);
    }

    private static void AccumulateRow(
        IAggregateAccumulator acc, Arena arena, in InvocationFrame frame,
        long id, string street, string city, string state, string zip)
    {
        acc.Accumulate(
        [
            DataValue.FromInt64(id),
            DataValue.FromString(street, arena),
            DataValue.FromString(city, arena),
            DataValue.FromString(state, arena),
            DataValue.FromString(zip, arena),
        ], frame);
    }

    private static void AccumulateThreeRows(IAggregateAccumulator acc, Arena arena, in InvocationFrame frame)
    {
        AccumulateRow(acc, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");
        AccumulateRow(acc, arena, frame, 2, "123 Nowhere St", "Springfield", "XX", "00000");
        AccumulateRow(acc, arena, frame, 3, "200 High St", "Columbus", "OH", "43215");
    }

    /// <summary>Three-company table whose rows pair with <see cref="ThreeRowResponse"/>.</summary>
    private static void SeedCompanies(TableCatalog catalog)
    {
        catalog.Plan("CREATE TABLE companies (id Int64, name String, street String, city String, state String, zip String)");
        catalog.Plan("INSERT INTO companies VALUES "
            + "(1, 'Acme', '100 Main St', 'Columbus', 'OH', '43215'),"
            + "(2, 'Ghost LLC', '123 Nowhere St', 'Springfield', 'XX', '00000'),"
            + "(3, 'HighCo', '200 High St', 'Columbus', 'OH', '43215')");
    }

    private static (int Id, int Status, int MatchType, int MatchedAddress, int Lat, int Lon) FieldIndexes(
        DataValue element, TypeRegistry types)
    {
        Assert.NotEqual(0, (int)element.TypeId);
        TypeDescriptor? desc = types.GetDescriptor(element.TypeId);
        Assert.NotNull(desc?.Fields);
        int id = desc!.FindFieldIndex("id");
        int status = desc.FindFieldIndex("status");
        int matchType = desc.FindFieldIndex("match_type");
        int matchedAddress = desc.FindFieldIndex("matched_address");
        int lat = desc.FindFieldIndex("lat");
        int lon = desc.FindFieldIndex("lon");
        Assert.True(id >= 0 && status >= 0 && matchType >= 0 && matchedAddress >= 0 && lat >= 0 && lon >= 0,
            "result struct must carry id/status/match_type/matched_address/lat/lon fields");
        return (id, status, matchType, matchedAddress, lat, lon);
    }

    // ───────────────────────── metadata ─────────────────────────

    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("census_geocode_agg", CensusGeocodeAggregateFunction.Name);
        Assert.Equal(FunctionCategory.Aggregate, CensusGeocodeAggregateFunction.Category);
    }

    [Fact]
    public void ValidateArguments_FiveAndSixArgForms()
    {
        CensusGeocodeAggregateFunction fn = new();
        DataKind[] five = [DataKind.Int64, DataKind.String, DataKind.String, DataKind.String, DataKind.String];
        DataKind[] fiveStringId = [DataKind.String, DataKind.String, DataKind.String, DataKind.String, DataKind.String];
        DataKind[] six = [.. five, DataKind.Struct];

        Assert.Equal(DataKind.Struct, fn.ValidateArguments(five));
        Assert.Equal(DataKind.Struct, fn.ValidateArguments(fiveStringId));
        Assert.Equal(DataKind.Struct, fn.ValidateArguments(six));

        Assert.Throws<ArgumentException>(() => fn.ValidateArguments(
            [DataKind.Float32, DataKind.String, DataKind.String, DataKind.String, DataKind.String]));
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments(
            [DataKind.Int64, DataKind.String, DataKind.Int32, DataKind.String, DataKind.String]));
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments(
            [.. five, DataKind.String]));
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments([DataKind.Int64, DataKind.String]));
    }

    [Fact]
    public void ResolveResultFields_MirrorsIdKind()
    {
        CensusGeocodeAggregateFunction fn = new();

        IReadOnlyList<ColumnInfo>? intFields = fn.ResolveResultFields(
            [DataKind.Int32, DataKind.String, DataKind.String, DataKind.String, DataKind.String]);
        Assert.NotNull(intFields);
        Assert.Equal(6, intFields!.Count);
        Assert.Equal("id", intFields[0].Name);
        Assert.Equal(DataKind.Int64, intFields[0].Kind);
        Assert.Equal(DataKind.String, intFields[1].Kind);
        Assert.Equal("lat", intFields[4].Name);
        Assert.Equal(DataKind.Float64, intFields[4].Kind);
        Assert.Equal(DataKind.Float64, intFields[5].Kind);

        IReadOnlyList<ColumnInfo>? stringFields = fn.ResolveResultFields(
            [DataKind.String, DataKind.String, DataKind.String, DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, stringFields![0].Kind);

        // Arity outside the signature → no declared shape.
        Assert.Null(fn.ResolveResultFields([DataKind.Int64]));
    }

    // ───────────────────────── result contract ─────────────────────────

    [Fact]
    public async Task IntIds_KeysResultsById_OutOfOrderResponse()
    {
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out _);
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();
        AccumulateThreeRows(acc, arena, frame);

        DataValue result = await acc.ResultAsync(frame);
        Assert.Equal(DataKind.Struct, result.Kind);
        Assert.True(result.IsArray);

        DataValue[] elements = result.AsStructArray(arena);
        Assert.Equal(3, elements.Length);
        var idx = FieldIndexes(elements[0], types);

        // Result order follows input order (1, 2, 3) even though the canned
        // response lists id 2 first.
        DataValue[] first = elements[0].AsStruct(arena);
        Assert.True(first[idx.Id].TryToInt64(out long firstId));
        Assert.Equal(1L, firstId);
        Assert.Equal("Match", first[idx.Status].AsString(arena));
        Assert.Equal("Exact", first[idx.MatchType].AsString(arena));
        Assert.Equal("100 MAIN ST, COLUMBUS, OH, 43215", first[idx.MatchedAddress].AsString(arena));
        Assert.Equal(39.9612, first[idx.Lat].AsFloat64(), 4);
        Assert.Equal(-83.0007, first[idx.Lon].AsFloat64(), 4);

        DataValue[] second = elements[1].AsStruct(arena);
        Assert.True(second[idx.Id].TryToInt64(out long secondId));
        Assert.Equal(2L, secondId);
        Assert.Equal("No_Match", second[idx.Status].AsString(arena));
        Assert.True(second[idx.MatchType].IsNull);
        Assert.True(second[idx.MatchedAddress].IsNull);
        Assert.True(second[idx.Lat].IsNull);
        Assert.True(second[idx.Lon].IsNull);

        DataValue[] third = elements[2].AsStruct(arena);
        Assert.True(third[idx.Id].TryToInt64(out long thirdId));
        Assert.Equal(3L, thirdId);
        Assert.Equal("Non_Exact", third[idx.MatchType].AsString(arena));
    }

    [Fact]
    public async Task StringIds_ReturnedAsStrings()
    {
        string response = "\"acme\",\"100 MAIN ST, COLUMBUS, OH, 43215\",\"No_Match\"\r\n";
        using ClientSwap swap = UseCannedResponse(response, out _);
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        acc.Accumulate(
        [
            DataValue.FromString("acme", arena),
            DataValue.FromString("100 Main St", arena),
            DataValue.FromString("Columbus", arena),
            DataValue.FromString("OH", arena),
            DataValue.FromString("43215", arena),
        ], frame);

        DataValue result = await acc.ResultAsync(frame);
        DataValue[] elements = result.AsStructArray(arena);
        var idx = FieldIndexes(elements[0], types);
        DataValue[] fields = elements[0].AsStruct(arena);
        Assert.Equal(DataKind.String, fields[idx.Id].Kind);
        Assert.Equal("acme", fields[idx.Id].AsString(arena));
    }

    [Fact]
    public async Task NullAddressParts_SentAsEmptyFields()
    {
        using ClientSwap swap = UseCannedResponse(
            "\"7\",\", , , 43215\",\"No_Match\"\r\n", out CannedHandler handler);
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        acc.Accumulate(
        [
            DataValue.FromInt64(7),
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.String),
            DataValue.FromString("43215", arena),
        ], frame);
        _ = await acc.ResultAsync(frame);

        string body = Assert.Single(handler.RequestBodies);
        Assert.Contains("7,,,,43215", body);
    }

    [Fact]
    public async Task Options_BenchmarkForwarded()
    {
        using ClientSwap swap = UseCannedResponse(
            "\"1\",\"100 MAIN ST, COLUMBUS, OH, 43215\",\"No_Match\"\r\n", out CannedHandler handler);
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        int stringType = types.InternScalarType(DataKind.String);
        ushort optionsTypeId = (ushort)types.InternStructType(
            [new StructFieldDescriptor("benchmark", stringType)]);
        DataValue options = DataValue.FromStruct(
            [DataValue.FromString("Public_AR_Census2020", arena)], arena, optionsTypeId);

        acc.Accumulate(
        [
            DataValue.FromInt64(1),
            DataValue.FromString("100 Main St", arena),
            DataValue.FromString("Columbus", arena),
            DataValue.FromString("OH", arena),
            DataValue.FromString("43215", arena),
            options,
        ], frame);
        _ = await acc.ResultAsync(frame);

        string body = Assert.Single(handler.RequestBodies);
        Assert.Contains("Public_AR_Census2020", body);
    }

    // ───────────────────────── null / error handling ─────────────────────────

    [Fact]
    public void NullId_Throws()
    {
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        Assert.Throws<FunctionArgumentException>(() => acc.Accumulate(
        [
            DataValue.Null(DataKind.Int64),
            DataValue.FromString("100 Main St", arena),
            DataValue.FromString("Columbus", arena),
            DataValue.FromString("OH", arena),
            DataValue.FromString("43215", arena),
        ], frame));
    }

    [Fact]
    public void DuplicateId_Throws()
    {
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();
        AccumulateRow(acc, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");

        Assert.Throws<FunctionArgumentException>(
            () => AccumulateRow(acc, arena, frame, 1, "200 High St", "Columbus", "OH", "43215"));
    }

    [Fact]
    public async Task EmptyGroup_ReturnsNull()
    {
        var (_, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task MissingIdInResponse_ThrowsRemoteService()
    {
        // Response answers id 1 only; id 2 was submitted but never answered.
        string response = "\"1\",\"100 MAIN ST, COLUMBUS, OH, 43215\",\"No_Match\"\r\n";
        using ClientSwap swap = UseCannedResponse(response, out _);
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();
        AccumulateRow(acc, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");
        AccumulateRow(acc, arena, frame, 2, "200 High St", "Columbus", "OH", "43215");

        RemoteServiceException ex = await Assert.ThrowsAsync<RemoteServiceException>(
            async () => await acc.ResultAsync(frame));
        Assert.Contains("'2'", ex.Message);
    }

    [Fact]
    public async Task ResultAsync_CancelledFrame_ThrowsWithoutContactingService()
    {
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out CannedHandler handler);
        Arena arena = CreateArena();
        TypeRegistry types = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        InvocationFrame frame = InvocationFrame.Symmetric(arena, null, types, cts.Token);

        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();
        AccumulateRow(acc, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await acc.ResultAsync(frame));
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task HttpFailure_SurfacesAsRemoteService()
    {
        FailingHandler handler = new();
        using ClientSwap swap = new(new CensusGeocoderClient(handler, retryDelays: []));
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();
        AccumulateRow(acc, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");

        await Assert.ThrowsAsync<RemoteServiceException>(async () => await acc.ResultAsync(frame));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream error"),
            });
    }

    // ───────────────────────── lifecycle ─────────────────────────

    [Fact]
    public async Task Merge_MatchesSingleAccumulator()
    {
        var (arena, types, frame) = CreateContext();
        CensusGeocodeAggregateFunction fn = new();

        using (UseCannedResponse(ThreeRowResponse, out _))
        {
            IAggregateAccumulator whole = fn.CreateAccumulator();
            AccumulateThreeRows(whole, arena, frame);
            DataValue wholeResult = await whole.ResultAsync(frame);

            IAggregateAccumulator left = fn.CreateAccumulator();
            IAggregateAccumulator right = fn.CreateAccumulator();
            AccumulateRow(left, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");
            AccumulateRow(right, arena, frame, 2, "123 Nowhere St", "Springfield", "XX", "00000");
            AccumulateRow(right, arena, frame, 3, "200 High St", "Columbus", "OH", "43215");
            await left.MergeAsync(right, frame);
            DataValue mergedResult = await left.ResultAsync(frame);

            DataValue[] wholeElements = wholeResult.AsStructArray(arena);
            DataValue[] mergedElements = mergedResult.AsStructArray(arena);
            Assert.Equal(wholeElements.Length, mergedElements.Length);
            var idx = FieldIndexes(wholeElements[0], types);
            for (int i = 0; i < wholeElements.Length; i++)
            {
                DataValue[] w = wholeElements[i].AsStruct(arena);
                DataValue[] m = mergedElements[i].AsStruct(arena);
                Assert.True(w[idx.Id].TryToInt64(out long wId));
                Assert.True(m[idx.Id].TryToInt64(out long mId));
                Assert.Equal(wId, mId);
                Assert.Equal(w[idx.Status].AsString(arena), m[idx.Status].AsString(arena));
            }
        }
    }

    [Fact]
    public void MergeDuplicateId_Throws()
    {
        var (arena, _, frame) = CreateContext();
        CensusGeocodeAggregateFunction fn = new();
        IAggregateAccumulator left = fn.CreateAccumulator();
        IAggregateAccumulator right = fn.CreateAccumulator();
        AccumulateRow(left, arena, frame, 1, "100 Main St", "Columbus", "OH", "43215");
        AccumulateRow(right, arena, frame, 1, "200 High St", "Columbus", "OH", "43215");

        Assert.Throws<FunctionArgumentException>(
            () => left.MergeAsync(right, frame).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Reset_ReusesAccumulatorCleanly()
    {
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out _);
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new CensusGeocodeAggregateFunction().CreateAccumulator();

        AccumulateThreeRows(acc, arena, frame);
        _ = await acc.ResultAsync(frame);

        acc.Reset();

        // Ids 1–3 again — must not trip the duplicate guard after Reset.
        AccumulateThreeRows(acc, arena, frame);
        DataValue result = await acc.ResultAsync(frame);
        Assert.Equal(3, result.AsStructArray(arena).Length);
    }

    // ───────────────────────── end-to-end SQL ─────────────────────────

    [Fact]
    public async Task Sql_GeocodeUnnestAndJoinBack()
    {
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out _);
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE companies (id Int64, name String, street String, city String, state String, zip String)");
        catalog.Plan("INSERT INTO companies VALUES "
            + "(1, 'Acme', '100 Main St', 'Columbus', 'OH', '43215'),"
            + "(2, 'Ghost LLC', '123 Nowhere St', 'Springfield', 'XX', '00000'),"
            + "(3, 'HighCo', '200 High St', 'Columbus', 'OH', '43215')");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT geo.id AS id, c.name AS name, geo.status AS status, geo.lat AS lat, geo.lon AS lon "
            + "FROM ("
            + "  SELECT g.value.id AS id, g.value.status AS status, g.value.lat AS lat, g.value.lon AS lon "
            + "  FROM (SELECT census_geocode_agg(id, street, city, state, zip) AS results FROM companies) r "
            + "  CROSS JOIN unnest(r.results) g"
            + ") geo "
            + "JOIN companies c ON c.id = geo.id "
            + "ORDER BY id",
            catalog, store: arena);

        Assert.Equal(3, rows.Count);

        Assert.Equal("Acme", rows[0]["name"].AsString(arena));
        Assert.Equal("Match", rows[0]["status"].AsString(arena));
        Assert.Equal(39.9612, rows[0]["lat"].AsFloat64(), 4);
        Assert.Equal(-83.0007, rows[0]["lon"].AsFloat64(), 4);

        Assert.Equal("Ghost LLC", rows[1]["name"].AsString(arena));
        Assert.Equal("No_Match", rows[1]["status"].AsString(arena));
        Assert.True(rows[1]["lat"].IsNull);
        Assert.True(rows[1]["lon"].IsNull);

        Assert.Equal("HighCo", rows[2]["name"].AsString(arena));
        Assert.Equal("Match", rows[2]["status"].AsString(arena));
        Assert.Equal(39.9690, rows[2]["lat"].AsFloat64(), 4);
    }

    [Fact]
    public async Task Sql_CtasWithoutCasts_PersistsTypedColumns()
    {
        // The documented geocode-once recipe, no casts: the aggregate declares
        // its element shape (ResolveResultFields), the shape flows through the
        // derived table into unnest's output column, and field access plans
        // with concrete kinds — so CTAS persists Int64/String/Float64 columns.
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out _);
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        SeedCompanies(catalog);
        catalog.Plan(
            "CREATE TABLE company_geo AS "
            + "SELECT g.value.id AS id, g.value.status AS status, g.value.lat AS lat, g.value.lon AS lon "
            + "FROM (SELECT census_geocode_agg(id, street, city, state, zip) AS results FROM companies) r "
            + "CROSS JOIN unnest(r.results) g");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id, status, lat, lon FROM company_geo ORDER BY id",
            catalog, store: arena);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataKind.Int64, rows[0]["id"].Kind);
        Assert.True(rows[0]["id"].TryToInt64(out long firstId));
        Assert.Equal(1L, firstId);
        Assert.Equal("Match", rows[0]["status"].AsString(arena));
        Assert.Equal(DataKind.Float64, rows[0]["lat"].Kind);
        Assert.Equal(39.9612, rows[0]["lat"].AsFloat64(), 4);
        Assert.Equal(-83.0007, rows[0]["lon"].AsFloat64(), 4);
        Assert.True(rows[1]["lat"].IsNull);
        Assert.Equal(39.9690, rows[2]["lat"].AsFloat64(), 4);
    }

    [Fact]
    public async Task Sql_CtasPersistsGeocodeResults()
    {
        // The geocode-once recipe with explicit casts — no longer required now
        // that the aggregate declares its element shape, but still supported.
        using ClientSwap swap = UseCannedResponse(ThreeRowResponse, out _);
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        SeedCompanies(catalog);
        catalog.Plan(
            "CREATE TABLE company_geo AS "
            + "SELECT cast(g.value.id AS Int64) AS id, cast(g.value.status AS String) AS status, "
            + "cast(g.value.lat AS Float64) AS lat, cast(g.value.lon AS Float64) AS lon "
            + "FROM (SELECT census_geocode_agg(id, street, city, state, zip) AS results FROM companies) r "
            + "CROSS JOIN unnest(r.results) g");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT geo.id AS id, c.name AS name, geo.lat AS lat, geo.lon AS lon "
            + "FROM companies c "
            + "JOIN company_geo geo ON geo.id = c.id "
            + "WHERE geo.status = 'Match' "
            + "ORDER BY id",
            catalog, store: arena);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Acme", rows[0]["name"].AsString(arena));
        Assert.Equal(39.9612, rows[0]["lat"].AsFloat64(), 4);
        Assert.Equal("HighCo", rows[1]["name"].AsString(arena));
        Assert.Equal(-82.9988, rows[1]["lon"].AsFloat64(), 4);
    }
}
