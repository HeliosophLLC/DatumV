using DatumIngest.DatumFile.Sidecar;
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
        IReadOnlyList<IReadOnlyList<DataValue>> inputs,
        IValueStore inputStore,
        SidecarRegistry? sidecarRegistry,
        IValueStore targetStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Pass-through. ModelInvocationOperator stabilises returned values into the
        // output batch's arena when scattering, so the model itself doesn't need to
        // worry about arena routing — it just produces values with whatever payloads
        // they came in with.
        DataValue[] outputs = new DataValue[inputs.Count];
        for (int row = 0; row < inputs.Count; row++)
        {
            outputs[row] = inputs[row][0];
        }

        return Task.FromResult<IReadOnlyList<DataValue>>(outputs);
    }
}
