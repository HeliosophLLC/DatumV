using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Models;

/// <summary>
/// Trivial test backend: returns each input row's first column unchanged.
/// Used by Phase A's smoke tests to validate the planner hoist pass and the
/// <c>ModelInvocationOperator</c>'s gather/scatter without dragging in a real
/// inference runtime. Stays deterministic so CSE folding logic can be tested
/// against it.
/// </summary>
/// <remarks>
/// <para>
/// Synthetic backend — no model file, no GPU, no async work. The
/// <see cref="InferBatchAsync"/> implementation is a synchronous loop wrapped
/// in <see cref="Task.FromResult{T}"/> so the operator's await contract is
/// exercised without actually suspending.
/// </para>
/// </remarks>
public sealed class EchoModel : IModel
{
    /// <summary>Singleton — the model is stateless.</summary>
    public static readonly EchoModel Instance = new();

    private EchoModel() { }

    /// <inheritdoc />
    public string Name => "echo";

    /// <inheritdoc />
    public bool IsDeterministic => true;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds => [DataKind.String];

    /// <inheritdoc />
    public DataKind OutputKind => DataKind.String;

    /// <inheritdoc />
    public Task<IReadOnlyList<DataValue>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        IValueStore targetStore,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();

        // Pass-through: read the input string out of the ValueRef and re-materialise
        // as a DataValue in the target arena. Inputs arrived already-resolved
        // (the evaluator's ToValueRef did the arena/sidecar lift), so AsString()
        // is a direct managed-memory read.
        DataValue[] outputs = new DataValue[inputs.Count];
        for (int row = 0; row < inputs.Count; row++)
        {
            ValueRef value = inputs[row][0];
            outputs[row] = value.IsNull
                ? DataValue.Null(DataKind.String)
                : DataValue.FromString(value.AsString(), targetStore);
        }

        return Task.FromResult<IReadOnlyList<DataValue>>(outputs);
    }
}
