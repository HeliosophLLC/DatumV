using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Scalar-function adapter that lets <c>models.X(...)</c> dispatch through
/// the same <see cref="IScalarFunction"/> path as built-in functions and
/// procedural UDFs. Resolved lazily by <see cref="FunctionRegistry.TryGetScalar(QualifiedName)"/>
/// when the dictionary lookup misses but the name matches a registered
/// model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The planner's
/// <c>ModelInvocationHoister</c> normally extracts <c>models.X(...)</c> calls
/// from expressions and runs them through a batched async operator. In
/// procedural UDF bodies (which run at runtime per row, opaque to the
/// planner), the hoister never gets to the call. Without this adapter,
/// <c>models.X</c> in a body throws "Unknown function" at evaluation time.
/// </para>
/// <para>
/// <strong>Per-row dispatch.</strong> Procedural bodies invoke this adapter
/// once per row. Each call <c>IModel.InferBatchAsync</c> with a
/// single-row batch — no cross-row batching at this layer. Operators that
/// want batched throughput stay on the hoister + <c>ModelInvocationOperator</c>
/// path; this adapter is the fallback for unhoisted contexts where per-row
/// is the natural shape anyway (the body is already executing per row).
/// </para>
/// <para>
/// <strong>Argument shape.</strong> The first <c>InputKinds.Count</c>
/// arguments are required inputs in the order the model declares them; any
/// further arguments map to the entry's
/// <see cref="ModelCatalogEntry.OptionalArgKinds"/> as per-call hyperparameter
/// overrides. A call site that supplies fewer overrides than the entry
/// declares is fine — the model uses its construction-time defaults for the
/// trailing parameters.
/// </para>
/// </remarks>
internal sealed class ModelScalarFunction : IScalarFunction
{
    private readonly string _modelName;
    private readonly Func<ModelCatalog?> _catalogResolver;

    /// <summary>
    /// Creates an adapter for the model registered as
    /// <paramref name="modelName"/> in the catalog returned by
    /// <paramref name="catalogResolver"/>. The resolver is invoked lazily on
    /// every call so the catalog can be re-attached or replaced without
    /// invalidating cached adapters.
    /// </summary>
    public ModelScalarFunction(string modelName, Func<ModelCatalog?> catalogResolver)
    {
        _modelName = modelName;
        _catalogResolver = catalogResolver;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Validates arity (required inputs + optional overrides) and returns
    /// the model's declared <see cref="ModelCatalogEntry.OutputKind"/>. The
    /// argument <i>kinds</i> aren't checked against the entry's declared
    /// kinds here — that's the planner's job for hoisted call sites; in
    /// procedural bodies, kind mismatches surface at the model's own input
    /// validation when <see cref="ExecuteAsync"/> dispatches.
    /// </remarks>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ModelCatalogEntry entry = ResolveEntry();
        int required = entry.InputKinds.Count;
        int optional = entry.OptionalArgKinds?.Count ?? 0;
        if (argumentKinds.Length < required || argumentKinds.Length > required + optional)
        {
            string expected = optional == 0
                ? $"{required} argument(s)"
                : $"{required}–{required + optional} argument(s)";
            throw new InvalidOperationException(
                $"Model 'models.{_modelName}' expects {expected}, got {argumentKinds.Length}.");
        }
        return entry.OutputKind;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Mirrors <see cref="ModelCatalogEntry.IsDeterministic"/>: a model whose
    /// output depends only on its inputs is pure, so the planner's CSE pass
    /// can dedupe identical call sites. Nondeterministic models (LLMs with
    /// temperature, diffusion without a fixed seed) report
    /// <see langword="false"/> and force one dispatch per call site.
    /// </remarks>
    public bool IsPure => ResolveEntry().IsDeterministic;

    /// <inheritdoc/>
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ModelCatalog catalog = _catalogResolver()
            ?? throw new InvalidOperationException(
                $"models.{_modelName} cannot be invoked: no ModelCatalog is attached to the function registry.");

        using ModelLease lease = await catalog
            .AcquireAsync(_modelName, cancellationToken)
            .ConfigureAwait(false);
        IModel model = lease.Model;

        // The model's required-input arity is authoritative: the first N
        // arguments map 1:1 to InputKinds, and the rest are per-call
        // hyperparameter overrides in OptionalArgKinds order.
        int required = model.InputKinds.Count;
        if (arguments.Length < required)
        {
            throw new InvalidOperationException(
                $"Model 'models.{_modelName}' requires {required} input(s), got {arguments.Length}.");
        }

        ValueRef[] rowInputs = new ValueRef[required];
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < required; i++) rowInputs[i] = args[i];

        IReadOnlyList<IReadOnlyList<ValueRef>> overrides;
        if (arguments.Length > required)
        {
            ValueRef[] rowOverrides = new ValueRef[arguments.Length - required];
            for (int i = 0; i < rowOverrides.Length; i++) rowOverrides[i] = args[required + i];
            overrides = new[] { (IReadOnlyList<ValueRef>)rowOverrides };
        }
        else
        {
            // Empty-overrides convention from the IModel contract.
            overrides = Array.Empty<IReadOnlyList<ValueRef>>();
        }

        IReadOnlyList<IReadOnlyList<ValueRef>> inputs = new[] { (IReadOnlyList<ValueRef>)rowInputs };
        // Thread the caller's TypeRegistry through so dynamic-shape model
        // outputs (procedural-adapter struct results) intern into the same
        // registry the downstream evaluator reads against. Frame.Types is
        // non-null for all real query paths; legacy callers with no
        // registry get a no-op forward through the default overload.
        IReadOnlyList<ValueRef> result = await model
            .InferBatchAsync(inputs, overrides, frame.Types, cancellationToken)
            .ConfigureAwait(false);

        if (result.Count == 0)
        {
            // Defensive: the contract is one output per input row, but a
            // misbehaving backend that returns nothing should yield a typed
            // null rather than an index-out-of-range exception.
            return ValueRef.Null(model.OutputKind);
        }
        return result[0];
    }

    private ModelCatalogEntry ResolveEntry()
    {
        ModelCatalog catalog = _catalogResolver()
            ?? throw new InvalidOperationException(
                $"models.{_modelName}: no ModelCatalog is attached to the function registry.");
        return catalog.TryGetEntry(_modelName)
            ?? throw new InvalidOperationException(
                $"Model '{_modelName}' is not registered. Register it via ModelCatalog.Register before referencing it from SQL.");
    }
}
