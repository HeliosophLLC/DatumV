using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Catalog;

/// <summary>
/// Returns the most recent <c>IDENTITY</c> value handed out for the named
/// table — i.e. the last value reserved by a prior <c>INSERT</c>.
/// Mirrors SQL Server's <c>IDENT_CURRENT('table')</c>.
/// </summary>
/// <remarks>
/// <para>
/// Returns <see cref="DataKind.Int64"/> NULL when:
/// <list type="bullet">
///   <item><description>The named table is not registered in the catalog.</description></item>
///   <item><description>The named table has no IDENTITY column.</description></item>
///   <item><description>No IDENTITY values have been reserved yet (the counter still sits at the seed).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Concurrency caveat:</b> this is a read-only peek at the persisted
/// IDENTITY counter. If two threads share the same <see cref="TableCatalog"/>
/// and one is mid-INSERT while the other calls <c>ident_current</c>, the
/// returned value reflects the most-recently-committed reservation —
/// in-flight reservations from an open <see cref="IAppendSession"/> are not
/// visible until the session commits. Same semantics as SQL Server's
/// <c>IDENT_CURRENT</c>.
/// </para>
/// <para>
/// <b>Why a catalog-bound instance:</b> normal scalar functions are hermetic
/// (they take only an <see cref="EvaluationFrame"/>). This function inspects
/// catalog state, so it gets registered via
/// <see cref="FunctionRegistry.RegisterScalarInstance"/> with the owning
/// <see cref="TableCatalog"/> captured at construction. Same pattern as the
/// future session-scoped <c>last_insert_id()</c>.
/// </para>
/// </remarks>
public sealed class IdentCurrentFunction : IFunction, IScalarFunction
{
    private readonly TableCatalog _catalog;

    /// <summary>
    /// Creates a catalog-bound <c>ident_current</c> function. The
    /// <paramref name="catalog"/> reference is captured for the lifetime
    /// of the function instance — typically the catalog's own lifetime.
    /// </summary>
    public IdentCurrentFunction(TableCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public static string Name => "ident_current";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns the most recently reserved IDENTITY value for the named table, "
        + "or NULL if the table has no IDENTITY column or no values have been reserved yet.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("table_name", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<IdentCurrentFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef tableNameArg = arguments.Span[0];
        if (tableNameArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int64));
        }

        string tableName = tableNameArg.AsString();
        if (!_catalog.TryGetTable(tableName, out ITableProvider? provider))
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int64));
        }

        IdentityState? state = provider.GetIdentityState();
        if (state is null)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int64));
        }

        // NextValue is the value the *next* reservation will return; the
        // last reserved value is one step earlier. When NextValue is still
        // at the seed, no values have been reserved yet — return NULL.
        if (state.NextValue == state.Spec.Seed)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int64));
        }

        return new ValueTask<ValueRef>(
            ValueRef.FromInt64(state.NextValue - state.Spec.Step));
    }

    /// <inheritdoc />
    public bool IsPure => false;
}
