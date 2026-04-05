using System.Runtime.CompilerServices;

using DatumIngest.Manifest;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>unnest(array) → table</c>. Expands an <c>Array&lt;T&gt;</c> argument
/// into a table with one row per element. The output schema has a single
/// column named <c>value</c> whose kind is the array's element kind.
/// </summary>
/// <remarks>
/// <para>
/// Useful for inspecting arrays element-by-element — most immediately for
/// previewing the per-frame Images that <c>animate_frames(...)</c>
/// produces, where each row in the unnested table is one rendered frame.
/// Generalises to any array-valued source: <c>SELECT * FROM unnest(arr)</c>
/// or <c>SELECT * FROM t CROSS APPLY unnest(t.tags)</c>.
/// </para>
/// <para>
/// <strong>Element shape:</strong> the unnest reads array elements via
/// <see cref="ValueRef.GetArrayElements"/>, which is the canonical path
/// for object-backed array payloads (anything with reference-typed
/// elements: <c>Image</c>, <c>Drawing</c>, <c>String</c>, <c>Struct</c>,
/// <c>Lambda</c>, …). Primitive-array payloads (<c>Int32[]</c>,
/// <c>Float32[]</c>, …) coming from
/// <see cref="ValueRef.FromPrimitiveArray{T}"/> aren't supported through
/// this path today; add a primitive-aware iteration branch when a real
/// use case demands it.
/// </para>
/// <para>
/// Null array input yields no rows (matches PostgreSQL semantics).
/// </para>
/// </remarks>
public sealed class UnnestFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <summary>
    /// Number of rows accumulated before yielding a batch. Matches the
    /// other TVF defaults; small enough that consumers see streaming
    /// behaviour for long arrays without unbounded latency on short ones.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "unnest";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Expands an Array<T> into a table of T values, one row per element. "
        + "Output column: 'value'. Use to inspect arrays element-by-element "
        + "(e.g. SELECT * FROM unnest(animate_frames(...))).";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            // Output column kind follows the array's element kind, which
            // can't be expressed in a static schema. Hover renders "→ table".
            FixedOutputSchema: null),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires exactly 1 argument: unnest(array).");
        }
        // argumentKinds[0] is the array's element kind (the framework passes
        // the kind component; the array-ness comes from the matcher's
        // ArrayMatch.Array gate during plan-time validation).
        DataKind elementKind = argumentKinds[0];
        return new Schema([new ColumnInfo("value", elementKind, nullable: true)]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("unnest() requires exactly 1 argument.");
        }

        ValueRef arr = arguments[0];
        if (arr.IsNull)
        {
            // PostgreSQL semantics: NULL array yields no rows.
            yield break;
        }
        if (!arr.IsArray)
        {
            throw new ArgumentException(
                $"unnest() expects an Array argument; got a scalar of kind {arr.Kind}.");
        }

        ColumnLookup lookup = new(["value"]);
        await foreach (RowBatch batch in EmitElementsAsync(arr, lookup, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> EmitElementsAsync(
        ValueRef arr,
        ColumnLookup lookup,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        // GetArrayElements returns the ValueRef[]-backed payload directly.
        // For Array<Image> from animate_frames (the immediate driver here),
        // this is exactly what we want. Primitive-array payloads aren't
        // covered today; see class remarks.
        //
        // Copy to a managed array because ReadOnlySpan<T> cannot cross the
        // yield/await boundary inside an iterator.
        ValueRef[] elements;
        try
        {
            elements = arr.GetArrayElements().ToArray();
        }
        catch (InvalidOperationException ex)
        {
            throw new NotSupportedException(
                "unnest() of primitive-array payloads (Int32[], Float32[], etc.) "
                + "is not currently supported through this path. The function works "
                + "on object-backed arrays (Image, Drawing, String, Struct, Lambda, …). "
                + "Restoring primitive-array support requires a per-element-kind iteration "
                + "branch.", ex);
        }

        for (int i = 0; i < elements.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(lookup);
            // Materialise the element ValueRef into a DataValue. For
            // reference-backed elements (Image, Drawing, etc.) this lands
            // their payload in the row batch's arena/sidecar as appropriate.
            DataValue value = elements[i].ToDataValue(batch.Arena);
            batch.Add([value]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}
