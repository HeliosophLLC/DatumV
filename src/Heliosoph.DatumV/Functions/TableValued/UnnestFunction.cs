using System.Formats.Cbor;
using System.Runtime.CompilerServices;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

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
        // JSON-scalar variant: treats the value's CBOR payload as a JSON array
        // and emits one Json row per element. Lets the user explore an
        // ingested JSON array column without first projecting into a typed
        // Array<T> shape — `SELECT * FROM coco, unnest(coco.annotations)`
        // gives one row per annotation.
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("json", DataKindMatcher.Exact(DataKind.Json), IsArray: ArrayMatch.Scalar),
            ],
            FixedOutputSchema: null),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
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
    public Schema ValidateArguments(
        ReadOnlySpan<ColumnInfo> argumentShapes,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentShapes.Length == 1
            && argumentShapes[0] is { Kind: DataKind.Struct, Fields: { Count: > 0 } fields })
        {
            // Array<Struct> input with a plan-time shape: the output element
            // keeps the field list so downstream field access (`x.value.id`)
            // and CTAS column derivation see concrete field kinds.
            return new Schema([new ColumnInfo("value", nullable: true, fields)]);
        }

        DataKind[] argumentKinds = new DataKind[argumentShapes.Length];
        for (int index = 0; index < argumentShapes.Length; index++)
        {
            argumentKinds[index] = argumentShapes[index].Kind;
        }
        return ValidateArguments(argumentKinds, constantArguments, constantStore, cancellationToken);
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

        ColumnLookup lookup = new(["value"]);

        // Json scalar input: decode the CBOR array and emit one Json row per
        // element. Distinct from typed-array input because the value isn't
        // IsArray — it's a scalar Json kind whose payload happens to be an
        // array. The signature matcher accepts this via the second variant.
        if (arr.Kind == DataKind.Json && !arr.IsArray)
        {
            await foreach (RowBatch batch in EmitJsonArrayElementsAsync(arr, lookup, context).ConfigureAwait(false))
            {
                yield return batch;
            }
            yield break;
        }

        if (!arr.IsArray)
        {
            throw new ArgumentException(
                $"unnest() expects an Array argument or a Json scalar; got a scalar of kind {arr.Kind}.");
        }

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
            // Forward the element's own TypeId + the per-query TypeRegistry so
            // struct elements keep their named-type stamp (LabeledDetection,
            // BoundingBox, …) and nested struct fields stay self-describing —
            // without this the row's struct DataValue lands with TypeId=0 and
            // the renderer falls back to f0/f1/f2 field names.
            DataValue value = elements[i].ToDataValue(batch.Arena, elements[i].TypeId, context.Types);
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

    /// <summary>
    /// Emits one <see cref="DataKind.Json"/> row per element of the JSON
    /// array carried by <paramref name="arr"/>'s CBOR payload. The output
    /// column kind matches the input — i.e. <c>Json</c> — so downstream
    /// queries can use the existing <c>json_*</c> function family (or the
    /// future dot-access desugaring) to drill into each element. Throws when
    /// the JSON value is not a top-level array — scalar/object roots have no
    /// row-multiplication semantics.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> EmitJsonArrayElementsAsync(
        ValueRef arr,
        ColumnLookup lookup,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        // Materialise to a managed byte[] so we can slice element windows out
        // of the same backing buffer without re-decoding. CborReader requires
        // ReadOnlyMemory; AsByteSpan gives us a span over the source already.
        byte[] cbor = arr.AsByteSpan().ToArray();
        int totalLen = cbor.Length;
        CborReader reader = new(cbor, CborConformanceMode.Canonical);

        if (reader.PeekState() != CborReaderState.StartArray)
        {
            throw new ArgumentException(
                $"unnest() on a Json value requires the value to be a JSON array; "
                + $"got CBOR state {reader.PeekState()}. Use json_value(...) to project "
                + "a sub-element first, or wrap the source in a JSON array.");
        }

        reader.ReadStartArray();
        RowBatch? batch = null;

        while (reader.PeekState() != CborReaderState.EndArray)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Element byte window: position before reading, then skip the
            // value to advance the cursor; the slice between is the element's
            // canonical CBOR encoding, ready to be stored directly as a Json
            // DataValue.
            int startPos = totalLen - reader.BytesRemaining;
            reader.SkipValue();
            int endPos = totalLen - reader.BytesRemaining;
            ReadOnlySpan<byte> elementSlice = cbor.AsSpan(startPos, endPos - startPos);

            batch ??= context.RentRowBatch(lookup);
            DataValue value = DataValue.FromJson(elementSlice, batch.Arena);
            batch.Add([value]);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        reader.ReadEndArray();

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}
