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

        await using IExportSink sink = _format.CreateSink(
            _target, _projectedSchema, _columnDispositions, _options);

        bool committed = false;
        try
        {
            await foreach (RowBatch batch in _sourcePlan
                .ExecuteAsync(cancellationToken, context)
                .ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                await sink.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            await sink.FinishAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
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

        yield break;
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
            columns[i] = new ColumnInfo(resolved.ColumnName, resolved.Kind, resolved.Nullable)
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
