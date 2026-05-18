using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Integration tests for <see cref="ITableValuedFunction.ValidateArguments"/>
/// as wired through <see cref="QuerySchemaResolver"/>. Verifies the
/// load-bearing invariants the FITS / HDF5 readers will lean on:
/// literal-in-source arguments surface as constants; bound parameter values
/// (after <see cref="ParameterBinder.Bind(SelectStatement, IReadOnlyDictionary{string, DataValue})"/>)
/// also surface as constants; column references and binary expressions do
/// not; output schemas selected from the constants flow into the resolved
/// column list; and plan-time peek failures fall back to an empty schema
/// without breaking sibling source resolution.
/// </summary>
public sealed class QuerySchemaResolverConstantArgsTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public QuerySchemaResolverConstantArgsTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-tvf-constargs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private TableCatalog OpenCatalog() => CreateCatalog(_catalogPath);

    private static (QuerySchemaResolver Resolver, StubConstantAwareTvf Stub) CreateWithStub(
        TableCatalog catalog,
        Func<DataValue?[], Schema>? schemaSelector = null,
        Exception? throwFromValidate = null)
    {
        StubConstantAwareTvf stub = new()
        {
            SchemaSelector = schemaSelector,
            ToThrow = throwFromValidate,
        };
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        registry.RegisterTableValued(stub);
        QuerySchemaResolver resolver = new(catalog, registry);
        return (resolver, stub);
    }

    private static SelectStatement ParseSelect(string sql)
    {
        QueryExpression query = SqlParser.Parse(sql);
        return ((SelectQueryExpression)query).Statement;
    }

    // ───────────────────── Constant population ─────────────────────

    [Fact]
    public async Task LiteralStringArg_PopulatesConstantSlot()
    {
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, StubConstantAwareTvf stub) = CreateWithStub(catalog);

        SelectStatement select = ParseSelect(
            "SELECT * FROM stub_const_tvf('/some/path/to/file.fits')");
        await resolver.ResolveAsync(select, CancellationToken.None);

        Assert.NotNull(stub.LastConstants);
        Assert.Single(stub.LastConstants);
        Assert.NotNull(stub.LastConstants[0]);
        Assert.Equal("/some/path/to/file.fits", stub.LastConstants[0]!.Value.AsString());
    }

    [Fact]
    public async Task LiteralIntArg_PopulatesConstantSlot()
    {
        // The parser folds small integers down to the narrowest fitting
        // type (sbyte / Int8 for 42), so we assert against Int8 specifically
        // rather than Int32 — what we care about is "the slot is populated
        // with the value 42", not which numeric kind it lands in.
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, StubConstantAwareTvf stub) = CreateWithStub(catalog);

        SelectStatement select = ParseSelect("SELECT * FROM stub_const_tvf(42)");
        await resolver.ResolveAsync(select, CancellationToken.None);

        Assert.NotNull(stub.LastConstants);
        Assert.Single(stub.LastConstants);
        Assert.NotNull(stub.LastConstants[0]);
        Assert.Equal(DataKind.Int8, stub.LastConstants[0]!.Value.Kind);
        Assert.Equal((sbyte)42, stub.LastConstants[0]!.Value.AsInt8());
    }

    [Fact]
    public async Task ColumnRefArg_LeavesConstantNull()
    {
        // Column references aren't plan-time-known. The slot must be null
        // so a runtime-schema TVF falls back to its default shape.
        using TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE TABLE paths (p String)");
        (QuerySchemaResolver resolver, StubConstantAwareTvf stub) = CreateWithStub(catalog);

        SelectStatement select = ParseSelect(
            "SELECT * FROM paths CROSS JOIN stub_const_tvf(paths.p)");
        await resolver.ResolveAsync(select, CancellationToken.None);

        Assert.NotNull(stub.LastConstants);
        Assert.Single(stub.LastConstants);
        Assert.Null(stub.LastConstants[0]);
    }

    [Fact]
    public async Task ParameterArg_AfterParameterBinder_PopulatesConstantSlot()
    {
        // The load-bearing invariant for FITS / HDF5 recipes: ParameterBinder
        // substitutes $archive into a LiteralExpression before planning, so
        // by the time QuerySchemaResolver runs the constant-args path fires
        // for both source-literal and bound-parameter calls uniformly.
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, StubConstantAwareTvf stub) = CreateWithStub(catalog);

        SelectStatement parsed = ParseSelect(
            "SELECT * FROM stub_const_tvf($archive)");
        // Short enough to fit DataValue's inline-string layout, so the test
        // can build the parameter DataValue without standing up a per-test
        // arena. The hook itself is shape-agnostic — the resolver's own
        // arena handles longer values when they show up through SQL parsing.
        const string parameterValue = "/x.fits";
        Dictionary<string, DataValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["archive"] = DataValue.FromString(parameterValue),
        };
        SelectStatement bound = ParameterBinder.Bind(parsed, parameters);

        await resolver.ResolveAsync(bound, CancellationToken.None);

        Assert.NotNull(stub.LastConstants);
        Assert.Single(stub.LastConstants);
        Assert.NotNull(stub.LastConstants[0]);
        Assert.Equal(parameterValue, stub.LastConstants[0]!.Value.AsString());
    }

    [Fact]
    public async Task SchemaSelectionByConstant_FlowsToResolvedColumns()
    {
        // End-to-end: TVF picks output schema based on the constant arg value,
        // and the chosen schema's columns flow through to ResolvedQuerySchema.
        // This is the real win — a literal-path TVF surfaces its actual
        // columns to downstream projection / WHERE / JOIN resolution.
        Schema schemaA = new(
        [
            new ColumnInfo("targetid", DataKind.Int64, nullable: false),
            new ColumnInfo("ra", DataKind.Float64, nullable: false),
        ]);
        Schema schemaFallback = new(
        [
            new ColumnInfo("fields", DataKind.String, nullable: false) { IsArray = true },
        ]);

        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, _) = CreateWithStub(catalog, schemaSelector: constants =>
        {
            if (constants[0]?.AsString() == "A") return schemaA;
            return schemaFallback;
        });

        ResolvedQuerySchema fromConstA = await resolver.ResolveAsync(
            ParseSelect("SELECT * FROM stub_const_tvf('A')"), CancellationToken.None);
        Assert.NotNull(fromConstA.FindColumn("targetid"));
        Assert.NotNull(fromConstA.FindColumn("ra"));
        Assert.Null(fromConstA.FindColumn("fields"));

        ResolvedQuerySchema fromOther = await resolver.ResolveAsync(
            ParseSelect("SELECT * FROM stub_const_tvf('B')"), CancellationToken.None);
        Assert.NotNull(fromOther.FindColumn("fields"));
        Assert.Null(fromOther.FindColumn("targetid"));
    }

    // ───────────────────── Exception widening ─────────────────────

    [Fact]
    public async Task FileNotFoundException_FromValidate_FallsBackToEmptySchema()
    {
        // A TVF that opens a file at plan time and throws FileNotFoundException
        // must not blow up the whole resolution. Same query without the bad
        // call shape should still surface columns from sibling sources.
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, _) = CreateWithStub(
            catalog,
            throwFromValidate: new FileNotFoundException("/does/not/exist.fits"));

        ResolvedQuerySchema resolved = await resolver.ResolveAsync(
            ParseSelect("SELECT * FROM stub_const_tvf('/does/not/exist.fits')"),
            CancellationToken.None);

        Assert.Empty(resolved.Columns);
    }

    [Fact]
    public async Task FunctionArgumentException_FromValidate_FallsBackToEmptySchema()
    {
        // Existing behavior — a kind mismatch surfaces as an empty schema rather
        // than propagating. The widened catch must not regress this.
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, _) = CreateWithStub(
            catalog,
            throwFromValidate: new FunctionArgumentException("stub_const_tvf", "bad arg"));

        ResolvedQuerySchema resolved = await resolver.ResolveAsync(
            ParseSelect("SELECT * FROM stub_const_tvf('anything')"), CancellationToken.None);

        Assert.Empty(resolved.Columns);
    }

    [Fact]
    public async Task InvalidOperationException_FromValidate_Propagates()
    {
        // We deliberately do NOT widen the catch to BCL "internal error"
        // exceptions — those usually signal a bug in the TVF and should not
        // be silently swallowed.
        using TableCatalog catalog = OpenCatalog();
        (QuerySchemaResolver resolver, _) = CreateWithStub(
            catalog,
            throwFromValidate: new InvalidOperationException("internal bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            ParseSelect("SELECT * FROM stub_const_tvf('x')"), CancellationToken.None));
    }

    // ───────────────────── Stub TVF ─────────────────────

    /// <summary>
    /// Records the kinds + constant arguments seen at validate time and
    /// returns either a configurable per-call schema, or a small default.
    /// Optionally throws a configured exception so we can exercise the
    /// resolver's catch-and-fallback path.
    /// </summary>
    private sealed class StubConstantAwareTvf : ITableValuedFunction
    {
        public string Name => "stub_const_tvf";

        public DataKind[]? LastKinds { get; private set; }
        public DataValue?[]? LastConstants { get; private set; }

        public Func<DataValue?[], Schema>? SchemaSelector { get; init; }
        public Exception? ToThrow { get; init; }

        public Schema ValidateArguments(
            ReadOnlySpan<DataKind> argumentKinds,
            ReadOnlySpan<DataValue?> constantArguments,
            IValueStore constantStore,
            CancellationToken cancellationToken)
        {
            LastKinds = argumentKinds.ToArray();
            LastConstants = constantArguments.ToArray();

            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            if (SchemaSelector is not null)
            {
                return SchemaSelector(LastConstants);
            }

            // Default schema — single Int32 column. Cheap and unambiguous.
            return new Schema([new ColumnInfo("v", DataKind.Int32, nullable: false)]);
        }

        public IAsyncEnumerable<RowBatch> ExecuteAsync(
            ValueRef[] arguments,
            ExecutionContext context)
            => Empty();

        private static async IAsyncEnumerable<RowBatch> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
