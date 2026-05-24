using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_csv_typed(path) → table</c>. Plan-time-typed sister of
/// <see cref="OpenCsvFunction"/>: scans the CSV at plan time to infer real
/// per-column types and surfaces them as a proper <see cref="Schema"/>, so
/// recipes can write <c>SELECT clip_id, duration_ms FROM open_csv_typed(...)</c>
/// against named, typed columns instead of positional
/// <c>fields[0]</c> / <c>fields[1]</c> indexing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time scan.</strong> <paramref>path</paramref> must be a
/// constant string at plan time (literal or bound <c>$parameter</c>). The
/// validator runs <see cref="CsvTypeScanner.ScanAsync"/> end-to-end — same
/// authoritative inference path the ingester uses — and builds a
/// <see cref="Schema"/> from the result. Non-constant paths throw
/// <see cref="FunctionArgumentException"/> so recipe authors inline the path
/// rather than threading it through a column reference.
/// </para>
/// <para>
/// <strong>Cost model.</strong> The scan is a full file pass. For interactive
/// <c>LIMIT 5</c> exploration on multi-GB CSVs this dominates wall time —
/// reach for <see cref="OpenCsvFunction"/> if you only need a quick look.
/// For ingest recipes (the common case) the scan is a one-shot price already
/// paid by the existing ingest pipeline, so this TVF gives recipes the same
/// type fidelity without forking the parser.
/// </para>
/// <para>
/// <strong>Why no <c>strict_types</c> opt-out (yet).</strong> The
/// pre-computed-scan ctor <see cref="CsvDeserializer(FileFormatDescriptor, CsvScanResult)"/>
/// hardcodes <c>strictTypes: true</c>. Wiring a sample-based opt-out means
/// running <c>HeaderDetector</c> at plan time (still touches the file, but
/// only ~100 rows) and threading a non-strict deserializer through; tracked
/// as a follow-up.
/// </para>
/// </remarks>
public sealed class OpenCsvTypedFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    // Cached across the validate→execute boundary so the file is scanned once
    // per query. Assumes the registry hands a fresh instance to each query;
    // verify before merging — if instances are pooled across queries this needs
    // to key by (path, mtime) or move to a per-query side table.
    private CsvScanResult? _scanResult;
    private string? _scanResultPath;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_csv_typed";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a CSV file with plan-time type inference and yields typed, named rows: " +
        "open_csv_typed(path). path must be a constant STRING. Runs the full ingest-grade " +
        "CSV scanner at plan time so the output schema carries real per-column types " +
        "(narrowed integers, dates, etc.) rather than the Array<String> shape returned by " +
        "open_csv.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
            ],
            FixedOutputSchema: null), // schema is path-dependent — computed in ValidateArguments
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 argument: open_csv_typed(path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        if (constantArguments[0] is not DataValue pathValue || pathValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be a constant STRING at plan time. " +
                "Recipes that need a runtime-bound path should inline the path string " +
                "rather than passing it through a column reference.");
        }

        string path = pathValue.AsString(constantStore);

        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name,
                $"file '{path}' does not exist or is not accessible.");
        }

        // Sync-over-async at plan time. ValidateArguments is sync by interface
        // contract; the scanner only exposes ScanAsync. Plan-time IO is already
        // sync in sibling TVFs (open_fits_table opens streams synchronously),
        // so this matches established style — but if the scanner ever becomes
        // CPU-bound enough to want a sync overload we should add one.
        using FileFormatDescriptor descriptor = new(path);
        CsvScanResult scan = CsvTypeScanner
            .ScanAsync(descriptor, cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (scan.ColumnNames.Length == 0)
        {
            throw new FunctionArgumentException(Name,
                $"CSV at '{path}' has no columns (empty file or unreadable header).");
        }

        _scanResult = scan;
        _scanResultPath = path;

        ColumnInfo[] columns = new ColumnInfo[scan.ColumnNames.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = new ColumnInfo(
                scan.ColumnNames[i],
                scan.Kinds[i],
                nullable: scan.NullCountsPerColumn[i] > 0);
        }
        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException(
                "open_csv_typed requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();

        // Reuse the plan-time scan when paths match. If they don't (because the
        // instance was reused across queries with different paths, or because
        // ValidateArguments wasn't called — e.g. a programmatic-API direct
        // invocation) fall back to scanning here.
        CsvScanResult scan;
        if (_scanResult is not null && _scanResultPath == path)
        {
            scan = _scanResult;
        }
        else
        {
            using FileFormatDescriptor scanDescriptor = new(path);
            scan = await CsvTypeScanner
                .ScanAsync(scanDescriptor, context.CancellationToken)
                .ConfigureAwait(false);
        }

        await foreach (RowBatch batch in StreamRowsAsync(path, scan, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        CsvScanResult scan,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileFormatDescriptor descriptor = new(path);
        CsvDeserializer deserializer = new(descriptor, scan);
        SerializationContext serContext = new(context.Pool);

        CancellationToken ct = cancellationToken == default ? context.CancellationToken : cancellationToken;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(serContext, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
