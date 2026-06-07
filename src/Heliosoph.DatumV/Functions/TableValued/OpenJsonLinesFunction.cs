using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Json;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_jsonl(path) → table</c>. Plan-time-typed file-path reader for
/// newline-delimited JSON (<c>.jsonl</c> / <c>.ndjson</c>). Runs
/// <see cref="JsonLinesDeserializer.ScanAsync"/> at plan time to lock the
/// file's shape and infer per-column types, then streams one row per non-empty
/// line at execute time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time scan.</strong> <paramref>path</paramref> must be a
/// constant string at plan time (literal or bound <c>$parameter</c>). The
/// validator walks the file once to detect shape from the first non-empty
/// line — if it's a JSON object the schema is the union of keys across all
/// lines with narrowed per-column kinds (object-mode); otherwise the schema
/// is a single column <c>value Json</c> (single-column-mode). Non-constant
/// paths throw <see cref="FunctionArgumentException"/>.
/// </para>
/// <para>
/// <strong>Cost model.</strong> Streaming end-to-end: pass 1 (scan) walks
/// every line, pass 2 (execute) walks them again. Memory stays bounded to
/// one line's worth of parsed JSON, so multi-GB JSONL files cost CPU + IO
/// but not RAM — preferred over <c>open_json</c> for large feeds.
/// </para>
/// <para>
/// <strong>Shape mismatches.</strong> A mid-file shape switch (an object
/// line in a single-column-mode file, or a non-object line in an object-mode
/// file) throws with the offending line number. Empty / whitespace-only
/// lines are skipped (matches <c>jq -c</c> conventions).
/// </para>
/// </remarks>
public sealed class OpenJsonLinesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    // Cached across the validate→execute boundary so the file is scanned once
    // per query. Assumes a fresh function instance per query; if instances are
    // ever pooled this needs to key by (path, mtime).
    private JsonLinesScanResult? _scanResult;
    private string? _scanResultPath;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_jsonl";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a JSON-Lines file (.jsonl / .ndjson) with plan-time type inference and " +
        "yields one row per non-empty line: open_jsonl(path). path must be a constant " +
        "STRING. If the first non-empty line is a JSON object the schema is the union " +
        "of keys across lines with narrowed per-column types; otherwise the schema is " +
        "a single column 'value' of kind Json carrying each line's value.";

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
                "requires 1 argument: open_jsonl(path).");
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

        // Sync-over-async at plan time; matches OpenCsvTypedFunction's pattern.
        using FileFormatDescriptor descriptor = new(path);
        JsonLinesScanResult scan = JsonLinesDeserializer
            .ScanAsync(descriptor, cancellationToken)
            .GetAwaiter()
            .GetResult();

        _scanResult = scan;
        _scanResultPath = path;

        return BuildSchema(scan, path);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException(
                "open_jsonl requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();

        JsonLinesScanResult scan;
        if (_scanResult is not null && _scanResultPath == path)
        {
            scan = _scanResult;
        }
        else
        {
            using FileFormatDescriptor scanDescriptor = new(path);
            scan = await JsonLinesDeserializer
                .ScanAsync(scanDescriptor, context.CancellationToken)
                .ConfigureAwait(false);
        }

        await foreach (RowBatch batch in StreamRowsAsync(path, scan, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static Schema BuildSchema(JsonLinesScanResult scan, string path)
    {
        if (scan.Mode == JsonLinesMode.SingleColumn)
        {
            // Single-column mode: every line is a non-object JSON value (string,
            // number, array, true/false/null). Each line lands as one Json cell;
            // the column itself is non-null at the storage layer — a JSON null
            // value round-trips as CBOR null inside the Json cell, not as SQL NULL.
            return new Schema(
            [
                new ColumnInfo("value", DataKind.Json, nullable: false),
            ]);
        }

        JsonScanResult objectScan = scan.ObjectScan
            ?? throw new InvalidOperationException(
                $"open_jsonl scan of '{path}' returned object-mode with no inner scan; this is a bug.");

        if (objectScan.ColumnNames.Length == 0)
        {
            throw new FunctionArgumentException(Name,
                $"JSONL file at '{path}' has no columns (lines were objects but contained no keys).");
        }

        ColumnInfo[] columns = new ColumnInfo[objectScan.ColumnNames.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = new ColumnInfo(
                objectScan.ColumnNames[i],
                objectScan.Kinds[i],
                nullable: objectScan.NullCountsPerColumn[i] > 0);
        }
        return new Schema(columns);
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        JsonLinesScanResult scan,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileFormatDescriptor descriptor = new(path);
        JsonLinesDeserializer deserializer = new(descriptor, scan);
        SerializationContext serContext = new(context.Pool);

        CancellationToken ct = cancellationToken == default ? context.CancellationToken : cancellationToken;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(serContext, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
