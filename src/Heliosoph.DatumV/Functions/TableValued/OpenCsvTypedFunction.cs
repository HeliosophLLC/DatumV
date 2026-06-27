using System.Collections.Concurrent;
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
    // per query. Keyed by path because FunctionRegistry holds a singleton
    // instance — every call site (including each arm of a multi-arm UNION
    // ALL within one query) hits the same TVF object. A single-slot field
    // would let arm-2's validate evict arm-1's scan before arm-1 executes,
    // forcing every arm to re-scan; a dictionary stores one slot per path
    // so each arm finds its own pre-warmed scan. Same semantics as the
    // original single-slot cache, just per-path. No mtime in the key
    // because catalog ingest reads each file once between download and
    // ingest — the file doesn't change underneath an in-flight query. If
    // long-running processes ever re-ingest a path that mutated on disk,
    // promote the key to (path, mtime, length).
    //
    // Options (skip_lines / comment / null_token) are NOT keyed here
    // because ExecuteAsync derives them from the runtime args directly,
    // not from any instance state — the path is the only stable identifier
    // shared between validate and execute.
    private static readonly ConcurrentDictionary<string, CsvScanResult> ScanCache = new();

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_csv_typed";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a CSV file with plan-time type inference and yields typed, named rows: " +
        "open_csv_typed(path [, skip_lines [, comment [, null_token [, header]]]]). path must be a " +
        "constant STRING. Runs the full ingest-grade CSV scanner at plan time so the " +
        "output schema carries real per-column types (narrowed integers, dates, etc.) " +
        "rather than the Array<String> shape returned by open_csv. " +
        "skip_lines drops the first N raw lines before delimiter / header detection " +
        "(e.g. 8 for a SEC EDGAR master.idx preamble). " +
        "comment is a single-character prefix; lines starting with it are dropped both " +
        "during the type scan and during row reading (e.g. '-' to skip the dashes " +
        "separator that follows the EDGAR header). " +
        "null_token names an extra unquoted literal treated as NULL by both passes " +
        "(e.g. '.' for FRED-style economic series, 'NA' for R-style exports). " +
        "header overrides the header-row autodetector: pass FALSE for headerless files " +
        "(columns are surfaced as col_0, col_1, …) or TRUE to force the first row to be " +
        "treated as headers; omit the argument to let the detector decide.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("skip_lines", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsOptional: true),
                new ParameterSpec("comment", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
                new ParameterSpec("null_token", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
                new ParameterSpec("header", DataKindMatcher.Exact(DataKind.Boolean), IsOptional: true),
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
        if (argumentKinds.Length is < 1 or > 5)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 to 5 arguments: open_csv_typed(path [, skip_lines [, comment [, null_token [, header]]]]).");
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

        int skipLines = 0;
        if (argumentKinds.Length >= 2)
        {
            if (!DataKindFamily.IntegerFamily.Contains(argumentKinds[1]))
            {
                throw new FunctionArgumentException(Name,
                    "argument 2 (skip_lines) must be an integer.");
            }
            if (constantArguments[1] is not DataValue skipValue)
            {
                throw new FunctionArgumentException(Name,
                    "argument 2 (skip_lines) must be a constant integer at plan time.");
            }
            long requested = skipValue.AsInt64();
            if (requested < 0 || requested > int.MaxValue)
            {
                throw new FunctionArgumentException(Name,
                    $"argument 2 (skip_lines) must be in [0, {int.MaxValue}]; got {requested}.");
            }
            skipLines = (int)requested;
        }

        string? comment = null;
        if (argumentKinds.Length >= 3)
        {
            if (argumentKinds[2] != DataKind.String)
            {
                throw new FunctionArgumentException(Name,
                    "argument 3 (comment) must be STRING.");
            }
            if (constantArguments[2] is not DataValue commentValue || commentValue.Kind != DataKind.String)
            {
                throw new FunctionArgumentException(Name,
                    "argument 3 (comment) must be a constant STRING at plan time.");
            }
            string c = commentValue.AsString(constantStore);
            // Single-character prefix only — anything wider crosses into "skip lines
            // matching a regex" territory which this option deliberately doesn't try
            // to be. Callers needing that should pre-process the file.
            if (c.Length != 1)
            {
                throw new FunctionArgumentException(Name,
                    $"argument 3 (comment) must be a single character; got '{c}' (length {c.Length}).");
            }
            comment = c;
        }

        string? nullToken = null;
        if (argumentKinds.Length >= 4)
        {
            if (argumentKinds[3] != DataKind.String)
            {
                throw new FunctionArgumentException(Name,
                    "argument 4 (null_token) must be STRING.");
            }
            if (constantArguments[3] is not DataValue tokenValue || tokenValue.Kind != DataKind.String)
            {
                throw new FunctionArgumentException(Name,
                    "argument 4 (null_token) must be a constant STRING at plan time.");
            }
            string token = tokenValue.AsString(constantStore);
            if (token.Length == 0)
            {
                throw new FunctionArgumentException(Name,
                    "argument 4 (null_token) must be non-empty; omit the argument to use " +
                    "the default (empty field and literal NULL are treated as null).");
            }
            nullToken = token;
        }

        bool? headerOverride = null;
        if (argumentKinds.Length >= 5)
        {
            if (argumentKinds[4] != DataKind.Boolean)
            {
                throw new FunctionArgumentException(Name,
                    "argument 5 (header) must be BOOLEAN.");
            }
            if (constantArguments[4] is not DataValue headerValue || headerValue.Kind != DataKind.Boolean)
            {
                throw new FunctionArgumentException(Name,
                    "argument 5 (header) must be a constant BOOLEAN at plan time.");
            }
            headerOverride = headerValue.AsBoolean();
        }

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
        using FileFormatDescriptor descriptor = new(path, BuildOptions(skipLines, comment, nullToken, headerOverride));
        CsvScanResult scan = CsvTypeScanner
            .ScanAsync(descriptor, cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (scan.ColumnNames.Length == 0)
        {
            throw new FunctionArgumentException(Name,
                $"CSV at '{path}' has no columns (empty file or unreadable header).");
        }

        ScanCache[path] = scan;

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
        if (arguments.Length is < 1 or > 5)
        {
            throw new ArgumentException(
                "open_csv_typed requires 1 to 5 arguments: (path [, skip_lines [, comment [, null_token [, header]]]]).");
        }

        string path = arguments[0].AsString();
        int skipLines = 0;
        string? comment = null;
        string? nullToken = null;
        bool? headerOverride = null;

        // Options always come from the runtime arguments — instance fields
        // would race across UNION ALL arms because the registry hands the
        // same singleton instance to every call site. ToInt64 (rather than
        // AsInt64) handles SQL integer literals of any width: '8' parses
        // as Int8 / UInt8 / etc., and the kind-strict AsInt64 would throw.
        if (arguments.Length >= 2 && !arguments[1].IsNull)
        {
            long requested = arguments[1].ToInt64();
            if (requested > 0) skipLines = requested > int.MaxValue ? int.MaxValue : (int)requested;
        }
        if (arguments.Length >= 3 && !arguments[2].IsNull)
        {
            string c = arguments[2].AsString();
            if (c.Length > 0) comment = c;
        }
        if (arguments.Length >= 4 && !arguments[3].IsNull)
        {
            string n = arguments[3].AsString();
            if (n.Length > 0) nullToken = n;
        }
        if (arguments.Length >= 5 && !arguments[4].IsNull)
        {
            headerOverride = arguments[4].AsBoolean();
        }

        // Reuse the plan-time scan stashed by ValidateArguments. Falls back
        // to scanning here when the cache misses — typically because
        // ValidateArguments wasn't called (programmatic-API direct invocation)
        // or because the entry was evicted (no eviction happens today, but
        // a future bound would land here).
        if (!ScanCache.TryGetValue(path, out CsvScanResult? scan))
        {
            using FileFormatDescriptor scanDescriptor = new(path, BuildOptions(skipLines, comment, nullToken, headerOverride));
            scan = await CsvTypeScanner
                .ScanAsync(scanDescriptor, context.CancellationToken)
                .ConfigureAwait(false);
        }

        await foreach (RowBatch batch in StreamRowsAsync(path, scan, skipLines, comment, nullToken, headerOverride, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        CsvScanResult scan,
        int skipLines,
        string? comment,
        string? nullToken,
        bool? headerOverride,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileFormatDescriptor descriptor = new(path, BuildOptions(skipLines, comment, nullToken, headerOverride));
        CsvDeserializer deserializer = new(descriptor, scan);
        SerializationContext serContext = new(context.Pool);

        CancellationToken ct = cancellationToken == default ? context.CancellationToken : cancellationToken;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(serContext, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static IReadOnlyDictionary<string, string>? BuildOptions(int skipLines, string? comment, string? nullToken, bool? headerOverride)
    {
        if (skipLines == 0 && comment is null && nullToken is null && headerOverride is null) return null;
        Dictionary<string, string> opts = new(4);
        if (skipLines > 0) opts["skip_lines"] = skipLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (comment is not null) opts["comment"] = comment;
        if (nullToken is not null) opts["null_token"] = nullToken;
        if (headerOverride is bool h) opts["header"] = h ? "true" : "false";
        return opts;
    }
}
