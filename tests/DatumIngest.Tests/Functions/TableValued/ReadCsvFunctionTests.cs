using System.Text;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>read_csv(bytes [, delimiter])</c> table-valued function: splits CSV
/// bytes into rows of <c>Array&lt;String&gt;</c> for positional projection.
/// Covers schema declaration, simple round-trips, line-ending variants,
/// custom delimiters, and the NULL / empty edge cases.
/// </summary>
public sealed class ReadCsvFunctionTests : ServiceTestBase
{
    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i]);
            }
        }
        return rows;
    }

    private static string[] FieldsOf(Row row, ExecutionContext ctx)
        => row["fields"].AsStringArray(ctx.Store);

    private static ValueRef BytesArg(string content) =>
        ValueRef.FromBytes(DataKind.UInt8, Encoding.UTF8.GetBytes(content), isArray: true);

    // ───────────────────────── Schema ─────────────────────────

    [Fact]
    public void ValidateArguments_DeclaresFieldsArrayStringSchema()
    {
        ReadCsvFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.UInt8]);

        Assert.Single(schema.Columns);
        Assert.Equal("fields", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.True(schema.Columns[0].IsArray);
        Assert.False(schema.Columns[0].Nullable);
    }

    [Fact]
    public void ValidateArguments_RejectsNonByteArrayInput()
    {
        ReadCsvFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]));
        Assert.Contains("bytes", ex.Message);
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArity()
    {
        ReadCsvFunction fn = new();
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([DataKind.UInt8, DataKind.String, DataKind.String]));
    }

    // ───────────────────────── Round-trip ─────────────────────────

    [Fact]
    public async Task ReadCsv_CommaDelimitedDefault_SplitsFieldsPerRow()
    {
        const string csv = "1,alpha,red\n2,beta,green\n3,gamma,blue\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg(csv)], ctx));

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "alpha", "red"], FieldsOf(rows[0], ctx));
        Assert.Equal(["2", "beta", "green"], FieldsOf(rows[1], ctx));
        Assert.Equal(["3", "gamma", "blue"], FieldsOf(rows[2], ctx));
    }

    [Fact]
    public async Task ReadCsv_PipeDelimited_LjSpeechShape_SplitsCorrectly()
    {
        // LJSpeech metadata.csv shape: `<id>|<transcript>|<normalized>` per line,
        // no header. This is the proximate driver for read_csv on bytes.
        const string manifest =
            "LJ001-0001|Printing was invented.|Printing was invented.\n" +
            "LJ001-0002|The art of dialling.|The art of dialling.\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [BytesArg(manifest), ValueRef.FromString("|")],
                ctx));

        Assert.Equal(2, rows.Count);
        string[] r0 = FieldsOf(rows[0], ctx);
        Assert.Equal("LJ001-0001", r0[0]);
        Assert.Equal("Printing was invented.", r0[1]);
        Assert.Equal("Printing was invented.", r0[2]);
        string[] r1 = FieldsOf(rows[1], ctx);
        Assert.Equal("LJ001-0002", r1[0]);
    }

    [Fact]
    public async Task ReadCsv_TabDelimited_CommonVoiceShape_SplitsCorrectly()
    {
        const string tsv =
            "client_id\tpath\tsentence\n" +
            "abc\tclips/1.mp3\tFirst utterance\n" +
            "def\tclips/2.mp3\tSecond utterance\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync(
                [BytesArg(tsv), ValueRef.FromString("\t")],
                ctx));

        Assert.Equal(3, rows.Count); // includes header line — user filters via WHERE
        Assert.Equal(["client_id", "path", "sentence"], FieldsOf(rows[0], ctx));
        Assert.Equal(["abc", "clips/1.mp3", "First utterance"], FieldsOf(rows[1], ctx));
    }

    // ───────────────────────── Line endings ─────────────────────────

    [Fact]
    public async Task ReadCsv_CrlfLineEndings_StripsCarriageReturn()
    {
        const string csv = "a,b,c\r\n1,2,3\r\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg(csv)], ctx));

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], FieldsOf(rows[0], ctx));
        Assert.Equal(["1", "2", "3"], FieldsOf(rows[1], ctx));
    }

    [Fact]
    public async Task ReadCsv_NoTrailingNewline_StillEmitsFinalRow()
    {
        const string csv = "a,b,c\n1,2,3"; // no trailing \n

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg(csv)], ctx));

        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2", "3"], FieldsOf(rows[1], ctx));
    }

    // ───────────────────────── Edge cases ─────────────────────────

    [Fact]
    public async Task ReadCsv_EmptyBytes_YieldsNoRows()
    {
        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg("")], ctx));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadCsv_NullBytes_YieldsNoRows()
    {
        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.Null(DataKind.UInt8)], ctx));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ReadCsv_MultiCharDelimiter_Throws()
    {
        const string csv = "a,b\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
        {
            await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                .ExecuteAsync([BytesArg(csv), ValueRef.FromString("||")], ctx))
            {
            }
        });
    }

    [Fact]
    public async Task ReadCsv_SingleFieldRow_ReturnsSingleElementArray()
    {
        const string csv = "alone\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg(csv)], ctx));

        Row row = Assert.Single(rows);
        Assert.Equal(["alone"], FieldsOf(row, ctx));
    }

    [Fact]
    public async Task ReadCsv_EmptyFieldsBetweenDelimiters_PreservedAsEmptyStrings()
    {
        const string csv = "a,,c\n";

        ReadCsvFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([BytesArg(csv)], ctx));

        Row row = Assert.Single(rows);
        Assert.Equal(["a", "", "c"], FieldsOf(row, ctx));
    }
}
