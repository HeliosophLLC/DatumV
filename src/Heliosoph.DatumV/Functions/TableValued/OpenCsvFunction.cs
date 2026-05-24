using System.Text;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_csv(path [, delimiter]) → table</c>. Streams a CSV file from disk
/// and yields one row per line, each row carrying a single <c>fields
/// Array&lt;String&gt;</c> column. The file-path analogue of
/// <see cref="ReadCsvFunction"/>: same output shape and parsing semantics,
/// but the bytes never round-trip through the query arena — useful for
/// large manifests that live on disk rather than inside an archive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When to reach for this vs. <c>read_csv</c>.</strong> Use
/// <c>read_csv</c> when the bytes already exist as an <c>Array&lt;UInt8&gt;</c>
/// in the query (typically the <c>bytes</c> column of <c>open_archive</c> or
/// <c>open_folder</c>). Use <c>open_csv</c> when the manifest is a loose file
/// on disk — avoids the byte-materialisation hop and streams line-by-line so
/// the working set stays bounded regardless of file size.
/// </para>
/// <para>
/// <strong>Schema parity with <c>read_csv</c>.</strong> Same single-column
/// output (<c>fields Array&lt;String&gt;</c>), same positional projection
/// pattern (<c>SELECT fields[0] AS clip_id, fields[1] AS transcript FROM
/// open_csv(...)</c>), same RFC 4180 quote handling via
/// <see cref="CsvLineSplitter"/> (wrapping quotes stripped, embedded
/// <c>""</c> collapsed). Recipes can swap between the two functions
/// without touching downstream projections.
/// </para>
/// <para>
/// <strong>Parser scope.</strong> Quoted fields with embedded delimiters
/// are handled; embedded newlines inside quoted fields are not (the line
/// reader splits the stream on bare <c>\n</c> before the splitter sees it).
/// For multi-line CSV cells, fall back to the typed file-path ingest.
/// </para>
/// <para>
/// Line endings: <c>\n</c> is the line separator; a trailing <c>\r</c> on
/// each line is stripped (handles <c>\r\n</c>-terminated payloads). A
/// trailing newline at EOF produces no extra row. Interior blank lines
/// emit a one-element <c>['']</c> row — same as <c>read_csv</c>.
/// </para>
/// <para>
/// <strong>File sharing.</strong> Opens with
/// <see cref="FileShare.ReadWrite"/> + <see cref="FileShare.Delete"/> so the
/// reader coexists with a manifest being appended to or rotated. UTF-8
/// decoding is assumed; non-UTF-8 payloads will surface garbled fields
/// rather than throwing, matching <c>read_csv</c>'s
/// <c>Encoding.UTF8.GetString</c> behaviour.
/// </para>
/// </remarks>
public sealed class OpenCsvFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(["fields"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_csv";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Streams a CSV file from disk into rows of Array<String>: " +
        "open_csv(path [, delimiter]). Each line becomes one row whose 'fields' " +
        "column carries the split values. Project columns positionally: fields[0], " +
        "fields[1], etc. Default delimiter is comma. RFC 4180 quoting handled " +
        "(wrapping quotes stripped, \"\" collapsed); embedded newlines inside " +
        "quoted fields are not. File-path analogue of read_csv (which takes bytes).";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("fields", DataKind.String, nullable: false) { IsArray = true },
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new FunctionArgumentException(Name,
                "requires 1 or 2 arguments: open_csv(path [, delimiter]).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (delimiter) must be STRING.");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length is not (1 or 2))
        {
            throw new ArgumentException(
                "open_csv requires 1 or 2 arguments: (path [, delimiter]).");
        }

        string path = arguments[0].AsString();

        char delimiter = ',';
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            string delimStr = arguments[1].AsString();
            if (delimStr.Length != 1)
            {
                throw new FunctionArgumentException(Name,
                    $"delimiter must be a single character; got '{delimStr}' (length {delimStr.Length}).");
            }
            delimiter = delimStr[0];
        }

        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name,
                $"file '{path}' does not exist or is not accessible.");
        }

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        // Stream the file rather than buffering. Coexist with appenders/rotators via
        // ReadWrite|Delete share semantics — same posture as TryReadFileIntoArena in
        // open_folder, except here we hand the stream to StreamReader for UTF-8 line
        // decoding instead of slurping bytes into the arena.
        await using FileStream fs = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            useAsync: true);
        using StreamReader reader = new(fs, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            string[] fields = CsvLineSplitter.Split(line, delimiter);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue fieldsValue = DataValue.FromStringArray(fields, batch.Arena);
            batch.Add([fieldsValue]);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }
}
