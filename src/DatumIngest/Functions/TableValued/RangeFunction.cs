using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// Generates a sequence of rows with a single <c>Value</c> column,
/// ranging from <c>start</c> to <c>end</c> (inclusive) with an optional
/// <c>step</c> (default 1).
/// </summary>
/// <remarks>
/// This is a virtual table source that requires no catalog registration.
/// Usage: <c>FROM RANGE(0, 360)</c> or <c>FROM RANGE(0, 1, 0.1) AS r</c>.
/// </remarks>
public sealed class RangeFunction : ISchemaAwareTableFunction
{
    private static readonly Schema OutputSchema = new(
        [new ColumnInfo("Value", DataKind.Float32, nullable: false)]);

    /// <summary>
    /// Number of rows accumulated before yielding a batch.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public string Name => "range";

    /// <inheritdoc />
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds)
    {
        return OutputSchema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        DataValue[] arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException("range() requires 2 or 3 arguments: range(start, end[, step]).");
        }

        float start = arguments[0].AsFloat32();
        float end = arguments[1].AsFloat32();
        float step = arguments.Length == 3 ? arguments[2].AsFloat32() : 1.0f;

        if (step == 0.0f)
        {
            throw new ArgumentException("range() step cannot be zero.");
        }

        if (step > 0 && start > end)
        {
            throw new ArgumentException("range() step must be negative when start > end.");
        }

        if (step < 0 && start < end)
        {
            throw new ArgumentException("range() step must be positive when start < end.");
        }

        string[] names = ["Value"];
        Dictionary<string, int> nameIndex = new(1, StringComparer.OrdinalIgnoreCase) { ["Value"] = 0 };

        // Use an integer counter to avoid floating-point drift accumulation.
        // Each value is computed as start + i * step, keeping precision stable.
        RowBatch? batch = null;

        if (step > 0)
        {
            for (int i = 0; ; i++)
            {
                float current = start + i * step;
                if (current > end + MathF.Abs(step) * 1e-5f)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                batch ??= RowBatch.Rent(DefaultBatchSize);
                batch.Add(new Row(names, [DataValue.FromFloat32(current)], nameIndex));
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }
        else
        {
            for (int i = 0; ; i++)
            {
                float current = start + i * step;
                if (current < end - MathF.Abs(step) * 1e-5f)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                batch ??= RowBatch.Rent(DefaultBatchSize);
                batch.Add(new Row(names, [DataValue.FromFloat32(current)], nameIndex));
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}
