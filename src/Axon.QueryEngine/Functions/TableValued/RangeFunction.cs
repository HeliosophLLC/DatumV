using System.Runtime.CompilerServices;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.TableValued;

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
        [new ColumnInfo("Value", DataKind.Scalar, nullable: false)]);

    /// <inheritdoc />
    public string Name => "range";

    /// <inheritdoc />
    public Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds)
    {
        return OutputSchema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> ExecuteAsync(
        DataValue[] arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (arguments.Length is not (2 or 3))
        {
            throw new ArgumentException("range() requires 2 or 3 arguments: range(start, end[, step]).");
        }

        float start = arguments[0].AsScalar();
        float end = arguments[1].AsScalar();
        float step = arguments.Length == 3 ? arguments[2].AsScalar() : 1.0f;

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

        // Use an integer counter to avoid floating-point drift accumulation.
        // Each value is computed as start + i * step, keeping precision stable.
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
                yield return new Row(names, [DataValue.FromScalar(current)]);
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
                yield return new Row(names, [DataValue.FromScalar(current)]);
            }
        }

        await Task.CompletedTask;
    }
}
