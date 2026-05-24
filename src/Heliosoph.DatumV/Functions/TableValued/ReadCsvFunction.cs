using System.Text;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>read_csv(bytes [, delimiter]) → table</c>. Parses CSV bytes into rows of
/// <c>Array&lt;String&gt;</c> — each input line becomes one row whose single
/// <c>fields</c> column carries the split field values. Designed for composition
/// with <c>open_archive</c> so SQL recipes can read a manifest from inside an
/// archive without ever touching disk:
/// <c>read_csv((SELECT bytes FROM open_archive(:source, 'meta.csv') LIMIT 1), '|')</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why <c>Array&lt;String&gt;</c> rather than named columns.</strong>
/// Named-column projection would require the output schema to be known at
/// plan time, but the column count and names are runtime-bound to the CSV
/// payload. Punching named columns into a TVF signature would force a richer
/// planner hook (constant-folding TVF args at plan time to peek at the
/// payload). Returning <c>Array&lt;String&gt;</c> sidesteps the planner work
/// — callers project columns positionally:
/// <code>
/// SELECT fields[0] AS clip_id, fields[1] AS transcript FROM read_csv(...);
/// </code>
/// When the planner grows constant-fold-on-validate we can add a richer
/// schema-bearing overload alongside this one without breaking existing
/// recipes.
/// </para>
/// <para>
/// <strong>Parser scope.</strong> RFC 4180 quoted fields are honoured via
/// <see cref="CsvLineSplitter"/> — wrapping <c>"..."</c> stripped, embedded
/// <c>""</c> collapsed to a single <c>"</c>, delimiters inside quotes
/// preserved. Embedded newlines inside quoted fields are <em>not</em>
/// supported: the splitter operates one line at a time, and the surrounding
/// loop breaks the payload on bare <c>\n</c> before quote state is
/// considered. Recipes that need multi-line CSV cells should reach for the
/// file-path ingest path.
/// </para>
/// <para>
/// Line endings: <c>\n</c> is the line separator; a trailing <c>\r</c> on each
/// line is stripped (handles <c>\r\n</c>-terminated payloads). The final
/// trailing newline produces no row.
/// </para>
/// </remarks>
public sealed class ReadCsvFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(["fields"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "read_csv";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Parses CSV bytes into rows of Array<String>: read_csv(bytes [, delimiter]). " +
        "Each line becomes one row whose 'fields' column carries the split values. " +
        "Project columns positionally: fields[0], fields[1], etc. Default delimiter is " +
        "comma. RFC 4180 quoting handled (wrapping quotes stripped, \"\" collapsed); " +
        "embedded newlines inside quoted fields are not.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
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
                "requires 1 or 2 arguments: read_csv(bytes [, delimiter]).");
        }
        if (argumentKinds[0] != DataKind.UInt8)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (bytes) must be Array<UInt8> — typically the bytes column of open_archive().");
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
                "read_csv requires 1 or 2 arguments: (bytes [, delimiter]).");
        }

        // NULL bytes yields no rows — matches PostgreSQL semantics for null inputs.
        if (arguments[0].IsNull)
        {
            yield break;
        }

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

        ReadOnlySpan<byte> bytes = arguments[0].AsByteSpan();
        // UTF-8 decode the whole payload up-front. For multi-MB manifests this is one
        // string allocation; the per-line split below operates on that string. Avoids
        // a stream-based decoder for the v1 simple case. If memory pressure on huge
        // manifests becomes an issue, swap for a streaming decoder + line reader.
        string content = Encoding.UTF8.GetString(bytes);

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        int lineStart = 0;
        for (int i = 0; i <= content.Length; i++)
        {
            // Treat end-of-string the same as a final \n so the last line emits if
            // the payload didn't end with a newline.
            bool atEnd = i == content.Length;
            bool atLineBreak = !atEnd && content[i] == '\n';
            if (!atEnd && !atLineBreak) continue;

            cancellationToken.ThrowIfCancellationRequested();

            int lineEnd = i;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
            {
                lineEnd--; // strip trailing \r for \r\n line endings
            }

            // Skip empty final lines (trailing newline at EOF) but preserve interior
            // empties — they're legitimate "fully-blank row" entries.
            if (atEnd && lineEnd == lineStart)
            {
                lineStart = i + 1;
                continue;
            }

            string line = content[lineStart..lineEnd];
            string[] fields = CsvLineSplitter.Split(line, delimiter);

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue fieldsValue = DataValue.FromStringArray(fields, batch.Arena);
            batch.Add([fieldsValue]);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }

            lineStart = i + 1;
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}
