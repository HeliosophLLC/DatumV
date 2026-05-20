using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Per-call context for <see cref="ExpressionEvaluator"/>. Carries the current row
/// together with the stores involved in expression evaluation:
/// <list type="bullet">
///   <item><description>
///     <see cref="Source"/> — the arena backing the current row's non-inline values.
///     Used whenever the evaluator reads a string, JSON, vector, array, or other
///     arena-backed reference-type payload from a row column.
///   </description></item>
///   <item><description>
///     <see cref="Target"/> — the arena where newly-materialized values are written
///     during this evaluation (e.g. a string literal in a predicate, a concatenation
///     result, a substring slice). Callers that need the result to outlive the
///     current row batch should pass a long-lived arena here; callers that write
///     their result straight into an output batch should pass that batch's arena.
///   </description></item>
///   <item><description>
///     <see cref="SidecarRegistry"/> — optional registry for resolving
///     <c>FlagInSidecar</c> DataValues (Large Binary Objects stored in
///     <c>.datum-blob</c> sidecars). Each value carries a <c>storeId</c> byte that
///     looks up the right <see cref="IBlobSource"/> here. Populated by frame builders
///     from <c>ExecutionContext.SidecarRegistry</c>; left <c>null</c> outside the
///     query pipeline.
///   </description></item>
/// </list>
/// The arenas are passed separately because the streaming pipeline typically reads
/// from one batch's arena and writes into another — mixing them would either pin
/// the source batch or write results into a soon-to-be-recycled arena.
/// </summary>
public readonly struct EvaluationFrame
{
    /// <summary>The row being evaluated.</summary>
    public Row Row { get; }

    /// <summary>Arena backing the row's non-inline column values. Read path.</summary>
    public IValueStore Source { get; }

    /// <summary>Arena into which newly-materialized values should be written. Write path.</summary>
    public IValueStore Target { get; }

    /// <summary>
    /// Plan-wide memory accountant. Procedural UDF / model bodies that
    /// construct an inner <see cref="VariableScope"/> wire it to this
    /// accountant so DECLARE'd payloads count against the surrounding plan's
    /// budget instead of an isolated per-call island. Future memory consumers
    /// added below the frame boundary read from here.
    /// </summary>
    public MemoryAccountant Accountant { get; }

    /// <summary>
    /// Optional outer row for correlated-subquery column resolution. Column references
    /// that cannot be resolved against <see cref="Row"/> fall back to this row.
    /// </summary>
    public Row? OuterRow { get; }

    /// <summary>
    /// Sidecar registry for resolving <c>FlagInSidecar</c> DataValues. The
    /// registry maps each value's <c>storeId</c> byte to the <see cref="IBlobSource"/>
    /// that backs its bytes; multi-table queries thread the same registry through every
    /// frame so joined rows can resolve cells from different sidecars correctly.
    /// </summary>
    public SidecarRegistry SidecarRegistry { get; }

    /// <summary>
    /// Per-query <see cref="TypeRegistry"/> for resolving struct-shape
    /// metadata (field names, nested type ids) from a <see cref="DataValue.TypeId"/>.
    /// Threaded by frame builders from <see cref="ExecutionContext.Types"/>.
    /// Functions consuming struct values look up <see cref="TypeDescriptor"/>s
    /// here so they can find fields by name instead of by position.
    /// </summary>
    public TypeRegistry Types { get; }

    /// <summary>
    /// Per-query <see cref="TypeIdTranslationTable"/> for translating a
    /// file's on-disk struct type-ids into <see cref="Types"/> ids. Sidecar-arm
    /// readers (<see cref="DataValue.AsStructArray"/>) consult this to resolve
    /// per-element TypeIds in <c>Array&lt;Struct&gt;</c> slot bytes. Threaded
    /// from <see cref="ExecutionContext.TypeIdTranslations"/>.
    /// </summary>
    public TypeIdTranslationTable TypeIdTranslations { get; }

    /// <summary>
    /// The currently-executing model body, when evaluation is inside a
    /// CREATE-MODEL UDF. The <c>infer()</c> scalar function reads this to
    /// find the bound <c>IInferenceSession</c>(s) it dispatches to —
    /// without a current model, <c>infer()</c> is a parse/runtime error
    /// because no session binding is in scope.
    /// </summary>
    /// <remarks>
    /// Set by the procedural-body executor when it enters a model body
    /// and cleared (back to <see langword="null"/>) when control returns
    /// to the surrounding scope. Nested model calls would push/pop a
    /// stack — for v1, model bodies are leaf (no model invokes another
    /// MODEL from inside its body), so the single field is sufficient.
    /// </remarks>
    public ModelDescriptor? CurrentModel { get; }

    /// <summary>
    /// Per-query registry of source videos backing <see cref="DataKind.VideoFrame"/>
    /// handles. Threaded from <see cref="ExecutionContext.VideoRegistry"/> by frame
    /// builders so scalar functions that materialise frames (e.g. <c>to_image</c>)
    /// can route handles through the warm FFmpeg decoder.
    /// </summary>
    public VideoRegistry VideoRegistry { get; }

    /// <summary>
    /// Optional handle to the evaluator-as-lambda-invoker. Set by the
    /// pipeline when the active query supports first-class lambdas;
    /// consumer functions (animation drivers, array transformations, etc.)
    /// call <see cref="ILambdaInvoker.InvokeLambdaAsync"/> through this
    /// slot. <see langword="null"/> for frames constructed outside the
    /// query pipeline — consumer functions that require lambda invocation
    /// should surface a clear error in that case rather than null-erroring.
    /// </summary>
    public ILambdaInvoker? LambdaInvoker { get; }

    /// <summary>
    /// The owning <see cref="ExecutionContext"/>. Carries the ambient
    /// query-wide state (accountant, registries, translations) that this
    /// frame's accessors mirror.
    /// </summary>
    public ExecutionContext Context { get; }

    /// <summary>
    /// Creates an evaluation frame with a single arena used for both reads
    /// and writes — the common shape, since the streaming pipeline's
    /// read-from-input / write-to-output split is currently theoretical
    /// (no live call site exercises it). Pulls the ambient accountant,
    /// sidecar / type / translation / video registries from
    /// <paramref name="context"/>.
    /// </summary>
    public EvaluationFrame(
        Row row,
        IValueStore store,
        ExecutionContext context,
        Row? outerRow = null,
        ModelDescriptor? currentModel = null,
        ILambdaInvoker? lambdaInvoker = null)
        : this(row, store, store, context, outerRow, currentModel, lambdaInvoker)
    {
    }

    /// <summary>
    /// Creates an evaluation frame with distinct source/target arenas, used
    /// when results must outlive the source row's arena (none of the
    /// current call sites need this — kept as a future extension point).
    /// Ambient state comes from <paramref name="context"/>.
    /// </summary>
    public EvaluationFrame(
        Row row,
        IValueStore source,
        IValueStore target,
        ExecutionContext context,
        Row? outerRow = null,
        ModelDescriptor? currentModel = null,
        ILambdaInvoker? lambdaInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Row = row;
        Source = source;
        Target = target;
        Accountant = context.Accountant;
        OuterRow = outerRow;
        SidecarRegistry = context.SidecarRegistry;
        Types = context.Types;
        TypeIdTranslations = context.TypeIdTranslations;
        CurrentModel = currentModel;
        VideoRegistry = context.VideoRegistry;
        LambdaInvoker = lambdaInvoker;
        Context = context;
    }

    /// <summary>
    /// Returns a new frame with a different <see cref="Row"/>, preserving the arenas,
    /// outer-row context, sidecar registry, type registry, translation table, accountant,
    /// current-model binding, and video registry. Used when the evaluator descends into
    /// a derived row (e.g. a lambda body's augmented row).
    /// </summary>
    public EvaluationFrame WithRow(Row row) =>
        new(row, Source, Target, Context, OuterRow, CurrentModel, LambdaInvoker);

    /// <summary>
    /// Returns a new frame with a <see cref="CurrentModel"/> binding,
    /// preserving everything else. Called by the procedural-body executor
    /// when entering a CREATE-MODEL body and (with <see langword="null"/>)
    /// when leaving it.
    /// </summary>
    public EvaluationFrame WithCurrentModel(ModelDescriptor? currentModel) =>
        new(Row, Source, Target, Context, OuterRow, currentModel, LambdaInvoker);

    /// <summary>
    /// Returns a new frame with a <see cref="LambdaInvoker"/> attached.
    /// Used by the pipeline when the query enters a lambda-supporting
    /// context (e.g. the evaluator wires itself as the invoker into every
    /// frame it dispatches through).
    /// </summary>
    public EvaluationFrame WithLambdaInvoker(ILambdaInvoker? lambdaInvoker) =>
        new(Row, Source, Target, Context, OuterRow, CurrentModel, lambdaInvoker);
}
