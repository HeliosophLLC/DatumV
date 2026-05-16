using System.Diagnostics;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Diagnostics;
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
/// <c>InferBatchAsync</c> — this adapter doesn't lower the body to a
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
/// lifecycle. <c>DROP MODEL</c> tears the descriptor down explicitly via
/// <c>LazyModelSessions.DisposeLoaded</c>; the adapter itself is now
/// <see cref="IDisposable"/> so <see cref="ModelResidencyManager"/>'s
/// eviction path can actually free VRAM by calling
/// <see cref="DatumIngest.Catalog.Registries.LazyModelSessions.Reset"/> —
/// dropping the cached sessions while leaving the descriptor + path map
/// intact for a future re-acquire to reload from disk. Re-acquires
/// always go through the loader, which pre-warms the sessions again, so
/// MIO-mediated invocations are safe across eviction churn. The known
/// gap is the scalar-fallback path (unhoisted <c>models.X(...)</c> calls
/// inside UDF bodies): those don't hold a residency lease and could
/// theoretically race a concurrent eviction. Tracked separately.
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
public sealed class ProceduralModelAdapter : IModel, IDisposable
{
    private readonly ModelDescriptor _descriptor;
    private readonly ProceduralModelFunction _function;
    private readonly TableCatalog _catalog;
    private readonly DataKind _outputKind;
    private readonly bool _outputIsArray;
    private readonly IReadOnlyList<DataKind> _inputKinds;
    private readonly IReadOnlyList<DataKind> _optionalKinds;
    private readonly bool _isBatchable;

    /// <summary>
    /// True when <paramref name="body"/> is "straight-line" — every statement
    /// is a <see cref="DeclareStatement"/>, <see cref="SetStatement"/>, or
    /// <see cref="ReturnStatement"/>, and the sequence ends with a RETURN.
    /// Anything that introduces a control-flow split (IF/WHILE/BLOCK/BREAK/
    /// CONTINUE) breaks the property: columnar evaluation can't pick a single
    /// branch to run for an N-row column when different rows would take
    /// different branches. The check is structural and cheap (one walk over
    /// the top-level statement list); nested expressions can call anything,
    /// because the body interpreter delegates per-expression evaluation to
    /// the scalar pipeline.
    /// </summary>
    public static bool IsStraightLineBody(IReadOnlyList<Statement> body)
    {
        if (body is null || body.Count == 0) return false;
        for (int i = 0; i < body.Count - 1; i++)
        {
            if (body[i] is not (DeclareStatement or SetStatement)) return false;
        }
        return body[^1] is ReturnStatement;
    }

    /// <summary>
    /// Builds an adapter for <paramref name="descriptor"/>. The
    /// <paramref name="catalog"/> is the catalog under which the model was
    /// registered; <c>InferBatchAsync</c> uses it to construct the
    /// per-call <see cref="DatumIngest.Execution.ExecutionContext"/> the body evaluates against
    /// (function registry, sidecar registry, pool). Pulling these from the
    /// catalog at call time rather than capturing each individually keeps
    /// the adapter aligned with how every other body-evaluator builds its
    /// ambient state.
    /// </summary>
    public ProceduralModelAdapter(ModelDescriptor descriptor, TableCatalog catalog)
    {
        _descriptor = descriptor;
        _catalog = catalog;
        _function = new ProceduralModelFunction(descriptor, catalog.Functions);

        // Output kind from the descriptor's RETURNS annotation. The parser
        // already validated this string at CREATE MODEL time; the
        // TryParse here is just a kind read. Capture the array bit too so
        // downstream consumers (catalog entry, manifest, hover / signature
        // help) can render <c>Array&lt;Float32&gt;</c> instead of the
        // stripped element kind.
        if (!TypeAnnotationResolver.TryParse(descriptor.ReturnTypeName, out DataKind returnKind, out bool returnIsArray))
        {
            throw new ArgumentException(
                $"Model '{descriptor.QualifiedName}': cannot resolve return type '{descriptor.ReturnTypeName}'.",
                nameof(descriptor));
        }
        _outputKind = returnKind;
        _outputIsArray = returnIsArray;

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
        _isBatchable = IsStraightLineBody(descriptor.StatementBody);
    }

    /// <inheritdoc />
    public string Name => _descriptor.Name;

    /// <inheritdoc />
    public bool IsDeterministic => false;

    // PreferredBatchSize is intentionally left at the IModel default
    // (null). The catalog's IBatchSizePolicy — DoublingBatchSizePolicy by
    // default — discovers the right batch size per model by ramping from
    // 1 and measuring VRAM delta at each step. Hardcoding a single value
    // here would either spill on VRAM-tight hosts (too high) or leave
    // throughput on the table on VRAM-rich hosts (too low). The policy's
    // measurement is per-model and per-host, so each model finds its own
    // ceiling without user configuration.

    /// <inheritdoc />
    /// <remarks>
    /// True iff the descriptor's body passes <see cref="IsStraightLineBody"/>.
    /// Computed once at construction; the body shape is immutable across the
    /// adapter's lifetime.
    /// </remarks>
    public bool IsBatchable => _isBatchable;

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds => _inputKinds;

    /// <inheritdoc />
    public DataKind OutputKind => _outputKind;

    /// <summary>
    /// True when the descriptor's <c>RETURNS</c> annotation declared an
    /// array type (<c>Array&lt;Float32&gt;</c>, <c>Float32[]</c>, …). Not part
    /// of <see cref="IModel"/> — exposed for the catalog-entry path so
    /// <see cref="ModelCatalogEntry.OutputIsArray"/> can be populated from
    /// the same parse as <see cref="OutputKind"/>.
    /// </summary>
    public bool OutputIsArray => _outputIsArray;

    /// <summary>
    /// Per-call hyperparameter kinds (parameters with defaults). Surfaces
    /// on the <see cref="ModelCatalogEntry.OptionalArgKinds"/> when the
    /// adapter is registered so MIO routes trailing call-site args to the
    /// <c>overrides</c> parameter of <c>InferBatchAsync</c>.
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
    public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
        // Legacy 3-arg overload: forwards to the registry-aware one with
        // a fresh registry. This path runs only when a caller invokes the
        // adapter directly without threading a TypeRegistry through —
        // legacy / test scaffolding. Engine callers (MIO, ModelScalarFunction)
        // always go through the 4-arg overload below.
        => InferBatchAsync(inputs, overrides, types: null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        TypeRegistry? types,
        CancellationToken cancellationToken)
    {
        int rowCount = inputs.Count;
        if (rowCount == 0) return Array.Empty<ValueRef>();

        // Single ephemeral arena reused across rows. Only touched on the
        // body's slow paths (cast-on-RETURN where the body's RETURN kind
        // doesn't match the declared RETURNS T). Released on exit.
        Arena arena = new();
        arena.AddReference();
        // Reuse the caller's TypeRegistry when supplied so struct shapes
        // built inside the body (e.g. `Array<Struct{label, score, x, y, w, h}>`
        // from a detector's post-processing function) intern into the
        // outer query's registry. Without this, the body's TypeIds resolve
        // against a private registry that MIO's scatter step doesn't see,
        // and downstream struct-field projection loses field names. Fall
        // back to a fresh registry only when there's no outer one
        // (legacy / test scaffolding).
        TypeRegistry typeRegistry = types ?? new TypeRegistry();
        // Per-call throwaway: IModel.InferBatchAsync does not yet thread
        // a MemoryAccountant. The body's residency is accounted in this
        // isolated island until the IModel API grows that parameter.
        using MemoryAccountant accountant = new();
        using Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: arena,
            types: typeRegistry,
            accountant: accountant, cancellationToken: cancellationToken);

        try
        {
            EvaluationFrame frame = context.CreateFrame(Row.Empty, arena);

            // Batched path: straight-line body + multi-row batch hits the
            // columnar interpreter so any in-body call that overrides
            // IScalarFunction.ExecuteBatchAsync (notably infer()) gets
            // its packed cross-row dispatch.
            if (_isBatchable && rowCount > 1)
            {
                using Activity? batchedSpan = DatumActivity.Scalars.StartActivity(
                    $"model.{_descriptor.Name}.batched(rows={rowCount})");
                ValueRef[] batched = await _function
                    .ExecuteModelBatchAsync(inputs, overrides, frame, cancellationToken)
                    .ConfigureAwait(false);
                return batched;
            }

            DatumActivity.Scalars.Trace(
                $"model.{_descriptor.Name}.per-row(rows={rowCount}, batchable={_isBatchable})");

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
    /// Releases the descriptor's bound ONNX sessions, freeing whatever
    /// VRAM they hold. The descriptor + path map stay intact so the next
    /// <c>InferBatchAsync</c> call can reload from disk through the
    /// adapter's loader path. Called by
    /// <see cref="ModelResidencyManager.EvictAlways"/> when this adapter
    /// is the resident <see cref="IModel"/> being evicted to make room
    /// for a sibling.
    /// </summary>
    public void Dispose()
    {
        _descriptor.BoundSessions.Reset();
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
