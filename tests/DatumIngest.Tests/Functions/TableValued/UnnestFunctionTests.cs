using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>unnest(array)</c> table-valued function: expands an Array&lt;T&gt;
/// into one row per element.
/// </summary>
/// <remarks>
/// Direct ExecuteAsync tests against pre-constructed Array&lt;T&gt;
/// arguments. The end-to-end SQL path (<c>SELECT value FROM unnest(...)</c>)
/// works for column references — see the user-facing examples in
/// <c>docs/functions/drawing.md</c>. The parser doesn't yet accept a
/// lambda literal nested inside a TVF call argument inside a CTE-select,
/// which limits the inline form of certain animate_frames patterns; SQL
/// users can sidestep with a two-statement approach (build the array
/// column, then unnest the column).
/// </remarks>
public sealed class UnnestFunctionTests : ServiceTestBase
{
    private static ValueRef[] MakeImageArray(int count)
    {
        ValueRef[] frames = new ValueRef[count];
        for (int i = 0; i < count; i++)
        {
            SKBitmap bmp = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (SKCanvas canvas = new(bmp))
            {
                canvas.Clear(new SKColor((byte)(i * 50), 100, 200, 255));
            }
            frames[i] = ValueRef.FromImage(bmp);
        }
        return frames;
    }

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches)
    {
        List<Row> rows = new();
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i]);
            }
        }
        return rows;
    }

    [Fact]
    public async Task Unnest_EmitsOneRowPerArrayElement()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Image, MakeImageArray(4));
        UnnestFunction fn = new();

        // The function exists; validate the output schema declares 'value' Image.
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.Image]);
        Assert.Single(schema.Columns);
        Assert.Equal("value", schema.Columns[0].Name);
        Assert.Equal(DataKind.Image, schema.Columns[0].Kind);

        // Execute against an ExecutionContext.
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([arr], ctx));

        Assert.Equal(4, rows.Count);
        foreach (Row row in rows)
        {
            Assert.Equal(DataKind.Image, row["value"].Kind);
            Assert.False(row["value"].IsNull);
        }
    }

    [Fact]
    public async Task Unnest_OnNullArray_YieldsNoRows()
    {
        // PG semantics: NULL array → empty result.
        ValueRef nullArr = ValueRef.NullArray(DataKind.Image);
        UnnestFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([nullArr], ctx));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_OnEmptyArray_YieldsNoRows()
    {
        ValueRef emptyArr = ValueRef.FromArray(DataKind.Image, Array.Empty<ValueRef>());
        UnnestFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([emptyArr], ctx));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_ScalarArgument_Throws()
    {
        ValueRef scalar = ValueRef.FromInt32(42);
        UnnestFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (RowBatch _ in ((ITableValuedFunction)fn).ExecuteAsync([scalar], ctx))
            {
                // pull the enumerator to force the exception
            }
        });
        Assert.Contains("expects an Array", ex.Message);
    }

    [Fact]
    public void Unnest_WrongArgumentCount_ThrowsOnValidate()
    {
        UnnestFunction fn = new();
        Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments([DataKind.Image, DataKind.Image]));
    }
}
