using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Models;

/// <summary>
/// Wraps a SQL-defined model (a <see cref="ModelDescriptor"/> registered via
/// <c>CREATE MODEL</c>) as an <see cref="IModel"/> so the planner's hoister
/// can lift <c>models.&lt;name&gt;(...)</c> calls into a
/// <c>ModelInvocationOperator</c>. From MIO's perspective the SQL-defined
/// model is indistinguishable from an engine-baked built-in: same residency
/// lease, same tracer hook, same <c>PreferredBatchSize</c> contract, same
/// streaming-sink routing. The body still runs per row inside
/// <see cref="InferBatchAsync"/> — this adapter doesn't lower the body to a
/// column pipeline (that's step 3, Option D). What it does buy is operator-
/// boundary parity with built-ins: tracer / RowLimit short-circuit /
/// streaming-sink awareness / sub-batching all light up immediately.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifecycle.</strong> Constructed at <c>CREATE MODEL</c> time and
/// stored on the underlying <see cref="ModelDescriptor"/>'s
/// <see cref="ModelCatalogEntry.Loader"/>. The descriptor's
/// <c>BoundSessions</c> own the ONNX <see cref="DatumIngest.Inference.IInferenceSession"/>
/// lifecycle; <c>DROP MODEL</c> disposes the sessions, and this adapter is
/// just a thin wrapper that becomes orphaned when the descriptor is gone.
/// Deliberately not <see cref="IDisposable"/>: an eviction from
/// <see cref="ModelResidencyManager"/> must not dispose anything (the
/// descriptor still references the same sessions via the scalar adapter
/// fallback path).
/// </para>
/// <para>
/// <strong>VRAM accounting.</strong> <see cref="ModelCatalogEntry.EstimatedVramBytes"/>
/// is set to <c>0</c> when this adapter is registered, and
/// <see cref="ModelCatalogEntry.RelativePath"/> is left <see langword="null"/> —
/// the residency manager's file-size heuristic doesn't fire. The underlying
/// inference session's footprint is owned by the descriptor's lifecycle
/// (load at CREATE MODEL, dispose at DROP MODEL), not the residency LRU.
/// If a future change moves session lifecycle into residency, this is the
/// place to set a real estimate.
/// </para>
/// <para>
/// <strong>Determinism.</strong> Always reports <see cref="IsDeterministic"/>
/// as <see langword="false"/>, mirroring <see cref="ProceduralModelFunction.IsPure"/>.
/// SQL-defined model bodies can interleave non-deterministic primitives
/// (random, current_timestamp, etc.) without flagging — the conservative
/// rule keeps the planner's CSE pass from collapsing distinct call sites
/// with equal arguments.
/// </para>
/// </remarks>
public sealed class ProceduralModelAdapter : IModel
{
    private readonly ModelDescriptor _descriptor;
    private readonly ProceduralModelFunction _function;
    private readonly DataKind _outputKind;
    private readonly IReadOnlyList<DataKind> _inputKinds;
    private readonly IReadOnlyList<DataKind> _optionalKinds;

    /// <summary>
    /// Builds an adapter for <paramref name="descriptor"/>. The function
    /// registry is the catalog's scalar registry — passed through to the
    /// body's evaluator so nested udf/scalar calls inside the body resolve
    /// the same way they would in a top-level expression.
    /// </summary>
    public ProceduralModelAdapter(ModelDescriptor descriptor, FunctionRegistry functions)
    {
        _descriptor = descriptor;
        _function = new ProceduralModelFunction(descriptor, functions);

        // Output kind from the descriptor's RETURNS annotation. The parser
        // already validated this string at CREATE MODEL time; the
        // TryParse here is just a kind read.
        if (!TypeAnnotationResolver.TryParse(descriptor.ReturnTypeName, out DataKind returnKind, out _))
        {
            throw new ArgumentException(
                $"Model '{descriptor.QualifiedName}': cannot resolve return type '{descriptor.ReturnTypeName}'.",
                nameof(descriptor));
        }
        _outputKind = returnKind;

        // Split parameters into required (no default) + optional (has
        // default). MIO's hoister routes the first segment as
        // InputExpressions and the rest as OptionalExpressions; this split
        // is what makes that routing line up with the call-site argument
        // count.
        List<DataKind> required = [];
        List<DataKind> optional = [];
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            UdfParameter p = descriptor.Parameters[i];
            DataKind kind = ResolveParamKind(p, descriptor.QualifiedName.ToString());
            if (p.Default is null) required.Add(kind);
            else optional.Add(kind);
        }
        _inputKinds = required;
        _optionalKinds = optional;
    }

    /// <inheritdoc />
    public string Name => _descriptor.Name;

    /// <inheritdoc />
    public bool IsDeterministic => false;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds => _inputKinds;

    /// <inheritdoc />
    public DataKind OutputKind => _outputKind;

    /// <summary>
    /// Per-call hyperparameter kinds (parameters with defaults). Surfaces
    /// on the <see cref="ModelCatalogEntry.OptionalArgKinds"/> when the
    /// adapter is registered so MIO routes trailing call-site args to the
    /// <c>overrides</c> parameter of <see cref="InferBatchAsync"/>.
    /// </summary>
    public IReadOnlyList<DataKind> OptionalKinds => _optionalKinds;

    /// <inheritdoc />
    /// <remarks>
    /// Each row's body executes in its own scoped invocation of
    /// <see cref="ProceduralModelFunction"/>. Inputs and overrides are
    /// concatenated as positional args before dispatch — that matches the
    /// body's expectation that parameters arrive in declaration order
    /// (required first, then any with defaults). The body itself runs
    /// ValueRef-native end-to-end after step 1, so the only arena writes
    /// per row are the (rare) cast-on-RETURN slow path.
    /// </remarks>
    public async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        int rowCount = inputs.Count;
        if (rowCount == 0) return Array.Empty<ValueRef>();

        // Single ephemeral arena reused across rows. Only touched on the
        // body's slow paths (cast-on-RETURN where the body's RETURN kind
        // doesn't match the declared RETURNS T). Released on exit.
        Arena arena = new();
        arena.AddReference();
        try
        {
            ValueRef[] results = new ValueRef[rowCount];
            for (int row = 0; row < rowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ValueRef> rowInputs = inputs[row];
                IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row
                    ? overrides[row]
                    : Array.Empty<ValueRef>();

                int suppliedCount = rowInputs.Count + rowOverrides.Count;
                ValueRef[] args = new ValueRef[suppliedCount];
                for (int i = 0; i < rowInputs.Count; i++) args[i] = rowInputs[i];
                for (int i = 0; i < rowOverrides.Count; i++) args[rowInputs.Count + i] = rowOverrides[i];

                EvaluationFrame frame = new(
                    Row.Empty,
                    arena,
                    arena,
                    outerRow: null,
                    sidecarRegistry: null,
                    types: null);

                results[row] = await _function
                    .ExecuteAsync(args.AsMemory(0, suppliedCount), frame, cancellationToken)
                    .ConfigureAwait(false);
            }
            return results;
        }
        finally
        {
            arena.ReleaseReference();
        }
    }

    /// <summary>
    /// Coerces a parameter's declared type annotation to a <see cref="DataKind"/>.
    /// Throws when the annotation is unparseable — same rule as
    /// <see cref="ProceduralModelFunction"/> applies, just lifted to
    /// construction time so the catalog entry's <see cref="InputKinds"/>
    /// is well-defined before any call site fires.
    /// </summary>
    private static DataKind ResolveParamKind(UdfParameter param, string modelName)
    {
        if (param.TypeName is null
            || !TypeAnnotationResolver.TryParse(param.TypeName, out DataKind kind, out _))
        {
            throw new ArgumentException(
                $"Model '{modelName}' parameter '@{param.Name}': cannot resolve type '{param.TypeName}'. " +
                "Use a recognised SQL type (Int32, Float64, Image, Array<Float32>, …) in the parameter declaration.",
                nameof(param));
        }
        return kind;
    }
}
