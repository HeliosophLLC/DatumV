using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// Takes an array-valued column (Vector or generic Array) and expands each
/// element into a separate row.
/// </summary>
/// <remarks>
/// Byte-array unnest (UInt8 + IsArray) requires a store-aware execution
/// context that the current TVF dispatch doesn't supply; that path throws at
/// runtime. The output-schema computation also can't infer "byte array" from
/// the kind alone (post-UInt8Array migration), so byte-array inputs fall
/// through to the default schema. Restored when the TVF interface grows
/// IsArray-aware schema info.
/// </remarks>
public sealed class UnnestFunction : IElementKindAwareTableFunction
{
    /// <summary>
    /// Number of rows accumulated before yielding a batch.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public string Name => "unnest";

    /// <inheritdoc />
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds) =>
        GetOutputSchema(argumentKinds, []);

    /// <inheritdoc />
    /// <remarks>
    /// When <paramref name="argumentKinds"/>[0] is <see cref="DataKind.Array"/> and
    /// <paramref name="arrayElementKinds"/>[0] is known, the output column uses the
    /// element kind directly. Without element kind metadata the fallback is String.
    /// </remarks>
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds, ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        if (argumentKinds.Length != 1)
        {
            return new Schema([new ColumnInfo("value", DataKind.Float32, nullable: true)]);
        }

        DataKind inputKind = argumentKinds[0];
        DataKind? elementKind = arrayElementKinds.Length > 0 ? arrayElementKinds[0] : null;

        return inputKind switch
        {
            // Use the element kind when it is known at plan time; otherwise fall
            // back to String. Float32 + IsArray (formerly Vector) reaches here as
            // DataKind.Float32 with elementKind = Float32 plumbed by the planner.
            DataKind.Array => elementKind is not null
                ? new Schema([new ColumnInfo("value", elementKind.Value, nullable: true)])
                : new Schema([new ColumnInfo("value", DataKind.String, nullable: true)]),

            // Byte-array inputs (UInt8 + IsArray) also reach here — see class remarks.
            _ => new Schema(
                [new ColumnInfo("value", DataKind.String, nullable: true)]),
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        DataValue[] arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("unnest() requires exactly 1 argument.");
        }

        DataValue input = arguments[0];
        if (input.IsNull)
        {
            yield break;
        }

        RowBatch? batch = null;

        switch (input.Kind)
        {
            // Float32 + IsArray (formerly DataKind.Vector). Inline arrays decode
            // without a store; arena-backed arrays need TVF dispatch to thread an
            // EvaluationFrame through — deferred until that interface lands.
            case DataKind.Float32 when input.IsArray:
            {
                if (!input.IsInlineArray)
                {
                    throw new NotSupportedException(
                        "unnest() of an arena-backed Float32 array requires a store-aware execution context "
                        + "and is not currently wired through the TVF dispatch path.");
                }
                // Materialise to a heap array because ReadOnlySpan<T> cannot cross
                // the yield/await boundary in this iterator.
                float[] values = input.AsInlineArraySpan<float>().ToArray();
                string[] names = ["value"];
                Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };
                foreach (float item in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= RowBatch.Rent(DefaultBatchSize);
                    batch.Add(new Row(names, [DataValue.FromFloat32(item)], nameIndex));
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
                break;
            }

            case DataKind.UInt8 when input.IsArray:
                // Byte arrays require a target IValueStore to resolve their bytes;
                // the current ExecuteAsync signature doesn't carry one. Restored
                // when the TVF interface gets a store/context parameter alongside
                // the other spill-operator migrations.
                throw new NotSupportedException(
                    "unnest() of a byte array requires a store-aware execution context "
                    + "and is not currently wired through the TVF dispatch path.");

            case DataKind.Array:
            {
                DataValue[] elements = input.AsArray();
                string[] names = ["value"];
                Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["value"] = 0 };
                foreach (DataValue element in elements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= RowBatch.Rent(DefaultBatchSize);
                    batch.Add(new Row(names, [element], nameIndex));
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
                break;
            }

            default:
                throw new ArgumentException($"unnest() does not support {input.Kind}.");
        }

        if (batch is not null)
        {
            yield return batch;
        }

        // Ensure the method is truly async to satisfy the compiler.
        await Task.CompletedTask;
    }

}
