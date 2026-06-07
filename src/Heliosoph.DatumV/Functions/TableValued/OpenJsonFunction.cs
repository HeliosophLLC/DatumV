using System.Runtime.CompilerServices;
using System.Text.Json;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Json;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_json(path) → table</c>. Plan-time-typed file-path reader for JSON
/// documents whose root is either a single object (one row) or an array of
/// objects (N rows). Runs <see cref="JsonTypeScanner"/> at plan time so the
/// output schema carries narrowed per-column kinds (UInt8 / Int64 / Float32 /
/// String / Boolean / Json for nested or mixed-family values) rather than a
/// generic <c>Array&lt;String&gt;</c> bag.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time scan.</strong> <paramref>path</paramref> must be a
/// constant string at plan time (literal or bound <c>$parameter</c>). The
/// validator opens the file, parses the document with <see cref="System.Text.Json"/>,
/// and runs the shared JSON type scanner — same authoritative inference path
/// the file ingester uses — to build a <see cref="Schema"/>. Non-constant paths
/// throw <see cref="FunctionArgumentException"/> so recipe authors inline the
/// path rather than threading it through a column reference.
/// </para>
/// <para>
/// <strong>Cost model.</strong> JSON documents are parsed in full to scan;
/// memory peaks at one document's worth of parsed tape. For multi-GB JSON
/// arrays prefer JSONL via <c>open_jsonl</c>, which streams line-by-line and
/// bounds peak memory to a single line's parsed shape.
/// </para>
/// <para>
/// <strong>Root shapes.</strong> A root object yields exactly one row; a root
/// array yields one row per element (each element must be an object).
/// Primitive or non-object roots throw with the file path in the message so
/// the source of bad input is identifiable. Nested objects/arrays and columns
/// whose scalar values mix primitive families across rows land as
/// <see cref="DataKind.Json"/> cells encoded as CBOR.
/// </para>
/// </remarks>
public sealed class OpenJsonFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    // Cached across the validate→execute boundary so the file is parsed and
    // scanned once per query. Assumes the registry hands a fresh instance to
    // each query; if instances are ever pooled across queries this needs to
    // key by (path, mtime) or move to a per-query side table.
    private JsonScanResult? _scanResult;
    private string? _scanResultPath;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_json";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a JSON file with plan-time type inference and yields typed, named rows: " +
        "open_json(path). path must be a constant STRING. The root must be a single object " +
        "(1 row) or an array of objects (N rows). Runs the ingest-grade JSON scanner at " +
        "plan time so the output schema carries narrowed per-column types (UInt8, Int64, " +
        "Float32, etc.); nested or mixed-family values land as Json cells.";

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
                "requires 1 argument: open_json(path).");
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

        using FileFormatDescriptor descriptor = new(path);
        JsonScanResult scan = ScanFile(descriptor, cancellationToken);

        if (scan.ColumnNames.Length == 0)
        {
            throw new FunctionArgumentException(Name,
                $"JSON at '{path}' has no columns (empty root object or empty array).");
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
                "open_json requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();

        // Reuse the plan-time scan when paths match. If they don't (because the
        // instance was reused across queries with different paths, or because
        // ValidateArguments wasn't called — e.g. a programmatic-API direct
        // invocation) fall back to scanning here.
        JsonScanResult scan;
        if (_scanResult is not null && _scanResultPath == path)
        {
            scan = _scanResult;
        }
        else
        {
            using FileFormatDescriptor scanDescriptor = new(path);
            scan = ScanFile(scanDescriptor, context.CancellationToken);
        }

        await foreach (RowBatch batch in StreamRowsAsync(path, scan, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static JsonScanResult ScanFile(FileFormatDescriptor descriptor, CancellationToken cancellationToken)
    {
        // Sync-over-async at plan time, mirroring OpenCsvTypedFunction. Plan-time
        // IO is already sync in sibling TVFs (OpenFitsTableFunction opens streams
        // synchronously).
        using Stream stream = descriptor
            .OpenAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        using JsonDocument document = JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .GetAwaiter()
            .GetResult();
        return JsonTypeScanner.Scan(EnumerateRows(document.RootElement, descriptor.FilePath), cancellationToken);
    }

    private static IEnumerable<JsonElement> EnumerateRows(
        JsonElement root, string filePath)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                yield return root;
                yield break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            $"JSON root array in '{filePath}' contains a non-object element at index {index} " +
                            $"(value kind: {element.ValueKind}). Only arrays of objects are supported.");
                    }
                    yield return element;
                    index++;
                }
                yield break;

            default:
                throw new InvalidDataException(
                    $"JSON root in '{filePath}' is {root.ValueKind}; expected an object or an array of objects.");
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        JsonScanResult scan,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using FileFormatDescriptor descriptor = new(path);
        JsonDeserializer deserializer = new(descriptor, scan);
        SerializationContext serContext = new(context.Pool);

        CancellationToken ct = cancellationToken == default ? context.CancellationToken : cancellationToken;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(serContext, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
