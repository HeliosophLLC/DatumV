using System.IO;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for <c>COPY (query) TO 'path' (...)</c>.
/// Composes a child <see cref="SelectPlan"/> for the source query under the
/// COPY node so EXPLAIN walks the full SELECT subtree; at execute time the
/// resolved <see cref="IExportFormat"/> opens an <see cref="IExportSink"/>
/// and the child plan's row stream is pumped through it.
/// </summary>
/// <remarks>
/// Plan-time work runs in <see cref="PlanAsync"/>: format resolution
/// (explicit FORMAT option, then extension inference); target shape
/// validation; per-column <see cref="MediaDisposition"/> resolution
/// (a typed-media column the format cannot represent fails here with a
/// specific message); inner-query planning. No file handle is opened
/// until <see cref="ExecuteImplAsync"/>.
/// </remarks>
internal sealed class ExportPlan : StatementPlan
{
    private readonly SelectPlan _sourcePlan;
    private readonly Schema _projectedSchema;
    private readonly IExportFormat _format;
    private readonly ExportTarget _target;
    private readonly ExportOptions _options;
    private readonly IReadOnlyList<MediaDisposition> _columnDispositions;
    private int _executed;

    private ExportPlan(
        TableCatalog catalog,
        SelectPlan sourcePlan,
        Schema projectedSchema,
        IExportFormat format,
        ExportTarget target,
        ExportOptions options,
        IReadOnlyList<MediaDisposition> columnDispositions)
        : base(catalog)
    {
        _sourcePlan = sourcePlan;
        _projectedSchema = projectedSchema;
        _format = format;
        _target = target;
        _options = options;
        _columnDispositions = columnDispositions;

        ExplainPlanNode tree = new()
        {
            OperatorName = "Copy",
            Details = DescribeTarget(target, format.Name, projectedSchema.Columns.Count),
            EstimatedRows = 0,
        };
        tree.Children.Add(sourcePlan.ExplainTree);
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    public override string Kind => "copy";

    /// <summary>
    /// Plan-time factory. Resolves the format from the COPY statement's
    /// option block (or path extension), evaluates per-column media
    /// dispositions, and plans the source query. Throws
    /// <see cref="ExportPlanException"/> for any user-facing problem — unknown
    /// format, ambiguous target shape, unsupported column kind, etc.
    /// </summary>
    public static async Task<StatementPlan> PlanAsync(
        TableCatalog catalog,
        CopyStatement copy,
        IExportFormatRegistry formatRegistry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(formatRegistry);

        ExportOptions options = ResolveOptions(copy.Options);
        IExportFormat format = ResolveFormat(copy, options, formatRegistry);

        ExportTarget target = format.RequiresDirectorySink
            ? new ExportTarget.Directory(copy.TargetPath)
            : new ExportTarget.File(copy.TargetPath);

        // Resolve the projection schema statically — mirrors CtasPlan. This
        // lets MediaDisposition + format-capability checks run before any
        // SELECT planning side effects.
        QuerySchemaResolver resolver = new(catalog, catalog.Functions);
        ResolvedQuerySchema projection = await resolver
            .ResolveProjectionAsync(copy.Source, outputAlias: "_copy_target", cancellationToken)
            .ConfigureAwait(false);

        Schema projectedSchema = BuildSchema(projection);

        MediaDisposition[] dispositions = new MediaDisposition[projectedSchema.Columns.Count];
        for (int i = 0; i < projectedSchema.Columns.Count; i++)
        {
            dispositions[i] = format.ResolveDisposition(projectedSchema.Columns[i], options);
        }

        SelectPlan sourcePlan = catalog.PlanQuery(copy.Source);

        return new ExportPlan(
            catalog, sourcePlan, projectedSchema, format, target, options, dispositions);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"ExportPlan for '{DescribeTargetPath(_target)}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to run it again.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // The sink is constructed lazily — see ReconcileSchemaWithRuntime.
        // QuerySchemaResolver can return DataKind.String as a fallback for
        // expressions it can't statically classify (notably model invocations
        // whose return type isn't visible to the static resolver). Trusting
        // that at sink-construction time produces a String encoder that then
        // tries to read non-String runtime values via AsString. Observing the
        // actual DataValue.Kind from the first non-empty batch fixes the
        // schema before the encoders bind to it.
        IExportSink? sink = null;
        bool committed = false;
        long rowsWritten = 0L;
        long bytesWritten = 0L;
        // Run the inner SELECT against a non-streaming context so its own
        // CellStarted / CellRowBatch / CellCompleted bracket doesn't fire —
        // otherwise the source's full row stream would land in the UI as a
        // separate "select" cell ahead of the summary, which is both noisy
        // (potentially millions of rows the user didn't ask to see) and
        // pushes the summary off the bottom of the results pane. The COPY
        // cell stays in the surrounding bracket so the summary row still
        // surfaces. Mirrors how ProceduralEvaluator silences internal
        // synthesized SELECTs (DECLARE / SET initialisers / IF predicates).
        using Execution.ExecutionContext sourceContext = context.WithoutStreaming();
        try
        {
            await foreach (RowBatch batch in _sourcePlan
                .ExecuteAsync(cancellationToken, sourceContext)
                .ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                if (sink is null)
                {
                    (Schema effectiveSchema, IReadOnlyList<MediaDisposition> effectiveDispositions) =
                        ReconcileSchemaWithRuntime(
                            _projectedSchema, _columnDispositions, _format, _options, batch);
                    sink = _format.CreateSink(
                        _target, effectiveSchema, effectiveDispositions, _options,
                        context.SidecarRegistry);
                }
                await sink.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            // Empty source — still produce a valid (empty) target so callers
            // can distinguish "the export ran" from "the export never started".
            sink ??= _format.CreateSink(
                _target, _projectedSchema, _columnDispositions, _options,
                context.SidecarRegistry);
            await sink.FinishAsync(cancellationToken).ConfigureAwait(false);
            // Capture the counts before Dispose runs — IExportSink doesn't
            // promise the properties remain readable after disposal, and the
            // summary RowBatch needs them after the finally block.
            rowsWritten = sink.RowsWritten;
            bytesWritten = sink.BytesWritten;
            committed = true;
        }
        finally
        {
            if (sink is not null)
            {
                await sink.DisposeAsync().ConfigureAwait(false);
            }
            // On any mid-stream failure best-effort delete a partially-written
            // single-file target so the catalog never surfaces an unreadable
            // file as if it were a successful export. Directory targets are
            // left to the sink's own cleanup since they may contain pre-existing
            // sibling files.
            if (!committed && _target is ExportTarget.File fileTarget)
            {
                try { File.Delete(fileTarget.Path); }
                catch { /* original exception wins */ }
            }
        }

        // Summary row — one batch with one row, two scalar Int64 columns. The
        // streaming layer treats this like any other plan output, so the UI's
        // results pane shows `rows_written` / `bytes_written` after a COPY
        // the same way it shows row counts after a SELECT. Matches DuckDB's
        // `COPY (…) TO …` return shape so anyone bouncing between engines
        // gets the expected feedback.
        RowBatch summary = context.RentRowBatch(SummaryLookup);
        DataValue[] row = context.Pool.RentDataValues(2);
        row[0] = DataValue.FromInt64(rowsWritten);
        row[1] = DataValue.FromInt64(bytesWritten);
        summary.Add(row);
        yield return summary;
    }

    /// <summary>
    /// <see cref="ColumnLookup"/> for the COPY summary RowBatch — a single
    /// row of <c>(rows_written, bytes_written)</c>. Allocated once at type
    /// load so every COPY shares the same instance; ColumnLookup is
    /// immutable-by-convention so this is safe to share.
    /// </summary>
    private static readonly ColumnLookup SummaryLookup = new(["rows_written", "bytes_written"]);

    /// <summary>
    /// Builds a corrected schema by observing the actual <see cref="DataValue.Kind"/>
    /// of each column in the first non-empty batch. Columns whose runtime kind
    /// matches the planner kind pass through unchanged; columns whose runtime
    /// kind differs get a fresh <see cref="ColumnInfo"/> with the runtime kind
    /// and a re-resolved <see cref="MediaDisposition"/>. Fully-NULL columns
    /// (no runtime kind observable in this batch) fall back to the planner
    /// kind — an edge case that only trips when the very first batch carries
    /// a NULL in every row of a deferred-kind column; downstream rows whose
    /// kind disagrees with the planner fallback still throw at append time.
    /// </summary>
    private static (Schema Schema, IReadOnlyList<MediaDisposition> Dispositions) ReconcileSchemaWithRuntime(
        Schema plannerSchema,
        IReadOnlyList<MediaDisposition> plannerDispositions,
        IExportFormat format,
        ExportOptions options,
        RowBatch batch)
    {
        ColumnLookup lookup = batch.ColumnLookup;
        ColumnInfo[] corrected = new ColumnInfo[plannerSchema.Columns.Count];
        MediaDisposition[] correctedDispositions = new MediaDisposition[plannerSchema.Columns.Count];
        bool anyChanged = false;

        for (int i = 0; i < plannerSchema.Columns.Count; i++)
        {
            ColumnInfo plannerCol = plannerSchema.Columns[i];
            corrected[i] = plannerCol;
            correctedDispositions[i] = plannerDispositions[i];

            if (!lookup.TryGetColumnOrdinal(plannerCol.Name, out int sourceOrd))
            {
                continue;
            }

            // Walk the batch looking for the first non-null value in this
            // column. A single null first row is common — defer to subsequent
            // rows within the same batch before falling back to the planner
            // kind.
            DataKind? observed = null;
            for (int r = 0; r < batch.Count; r++)
            {
                DataValue value = batch[r][sourceOrd];
                if (!value.IsNull)
                {
                    observed = value.Kind;
                    break;
                }
            }
            if (observed is null || observed.Value == plannerCol.Kind)
            {
                continue;
            }

            ColumnInfo runtimeCol = new(plannerCol.Name, observed.Value, plannerCol.Nullable)
            {
                IsArray = plannerCol.IsArray,
                IsMultiDim = plannerCol.IsMultiDim,
            };
            corrected[i] = runtimeCol;
            correctedDispositions[i] = format.ResolveDisposition(runtimeCol, options);
            anyChanged = true;
        }

        return anyChanged
            ? (new Schema(corrected), correctedDispositions)
            : (plannerSchema, plannerDispositions);
    }

    private static ExportOptions ResolveOptions(IReadOnlyList<CopyOption> astOptions)
    {
        Dictionary<string, object> bag = new(StringComparer.OrdinalIgnoreCase);
        foreach (CopyOption opt in astOptions)
        {
            if (opt.Value is not LiteralExpression literal)
            {
                throw new ExportPlanException(
                    $"COPY option '{opt.Key}': value must be a literal (string, number, or " +
                    "bare identifier). Expressions are not supported in the option block.");
            }
            if (bag.ContainsKey(opt.Key))
            {
                throw new ExportPlanException(
                    $"COPY option '{opt.Key}' specified more than once.");
            }
            bag[opt.Key] = literal.Value!;
        }
        return new ExportOptions(bag);
    }

    private static IExportFormat ResolveFormat(
        CopyStatement copy,
        ExportOptions options,
        IExportFormatRegistry registry)
    {
        if (options.GetString("FORMAT") is string formatName)
        {
            IExportFormat? byName = registry.ResolveByName(formatName);
            if (byName is null)
            {
                throw new ExportPlanException(
                    $"COPY: unknown format '{formatName}'. " +
                    $"Registered formats: {string.Join(", ", registry.All.Select(f => f.Name))}.");
            }
            return byName;
        }

        string extension = Path.GetExtension(copy.TargetPath);
        if (string.IsNullOrEmpty(extension))
        {
            throw new ExportPlanException(
                $"COPY TO '{copy.TargetPath}': no FORMAT option supplied and the target path has " +
                "no extension to infer one from. Add (FORMAT x) to the option block.");
        }
        IExportFormat? byExt = registry.ResolveByExtension(extension);
        if (byExt is null)
        {
            throw new ExportPlanException(
                $"COPY TO '{copy.TargetPath}': cannot infer format from extension '{extension}'. " +
                "Specify FORMAT explicitly in the option block.");
        }
        return byExt;
    }

    private static Schema BuildSchema(ResolvedQuerySchema projection)
    {
        if (projection.Columns.Count == 0)
        {
            throw new ExportPlanException("COPY: source query produces no columns.");
        }
        ColumnInfo[] columns = new ColumnInfo[projection.Columns.Count];
        for (int i = 0; i < projection.Columns.Count; i++)
        {
            ResolvedColumn resolved = projection.Columns[i];
            // Struct projections with known field metadata route to the
            // struct ColumnInfo so the Parquet sink can build a real
            // StructField. Without this branch a struct literal lands as
            // a fields-less Struct ColumnInfo and the encoder factory
            // rejects it at plan time.
            columns[i] = resolved.Kind == DataKind.Struct && resolved.Fields is { } fields
                ? new ColumnInfo(resolved.ColumnName, resolved.Nullable, fields)
                {
                    // Preserve IsArray for Array<Struct> projections — array
                    // literals like `[{a:1,b:'x'}]` resolve as Kind=Struct +
                    // IsArray=true + Fields populated. Without this the sink
                    // would see a scalar struct and try to encode one element.
                    IsArray = resolved.IsArray,
                    IsMultiDim = resolved.IsMultiDim,
                }
                : new ColumnInfo(resolved.ColumnName, resolved.Kind, resolved.Nullable)
                {
                    IsArray = resolved.IsArray,
                    IsMultiDim = resolved.IsMultiDim,
                };
        }
        return new Schema(columns);
    }

    private static string DescribeTarget(ExportTarget target, string formatName, int columnCount)
    {
        string path = DescribeTargetPath(target);
        return $"format={formatName}, target={path}, columns={columnCount}";
    }

    private static string DescribeTargetPath(ExportTarget target) => target switch
    {
        ExportTarget.File f => f.Path,
        ExportTarget.Directory d => d.Path,
        _ => "<unknown>",
    };
}
