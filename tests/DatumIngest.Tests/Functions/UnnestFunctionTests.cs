using DatumIngest.Functions.TableValued;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class UnnestFunctionTests
{
    private readonly UnnestFunction _function = new();

    [Fact]
    public void Name_IsUnnest()
    {
        Assert.Equal("unnest", _function.Name);
    }

    [Fact]
    public async Task Unnest_Vector_ExpandsToScalarRows()
    {
        DataValue vector = DataValue.FromVector([10, 20, 30]);
        List<Row> rows = await CollectRows([vector]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(10f, rows[0]["value"].AsFloat32());
        Assert.Equal(20f, rows[1]["value"].AsFloat32());
        Assert.Equal(30f, rows[2]["value"].AsFloat32());
    }

    [Fact]
    public async Task Unnest_UInt8Array_ExpandsToUInt8Rows()
    {
        DataValue bytes = DataValue.FromUInt8Array([1, 2, 3]);
        List<Row> rows = await CollectRows([bytes]);

        Assert.Equal(3, rows.Count);
        Assert.Equal((byte)1, rows[0]["value"].AsUInt8());
        Assert.Equal((byte)2, rows[1]["value"].AsUInt8());
        Assert.Equal((byte)3, rows[2]["value"].AsUInt8());
    }

    [Fact]
    public async Task Unnest_JsonArray_ScalarValues()
    {
        DataValue json = DataValue.FromJsonValue("[\"a\", \"b\", \"c\"]");
        List<Row> rows = await CollectRows([json]);

        Assert.Equal(3, rows.Count);
        Assert.Equal("a", rows[0]["value"].AsString());
        Assert.Equal("b", rows[1]["value"].AsString());
        Assert.Equal("c", rows[2]["value"].AsString());
    }

    [Fact]
    public async Task Unnest_JsonArrayOfObjects_ExpandsProperties()
    {
        DataValue json = DataValue.FromJsonValue(
            "[{\"item1\": 1, \"item2\": \"a\"}, {\"item1\": 2, \"item2\": \"b\"}]");
        List<Row> rows = await CollectRows([json]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["item1"].AsFloat32());
        Assert.Equal("a", rows[0]["item2"].AsString());
        Assert.Equal(2f, rows[1]["item1"].AsFloat32());
        Assert.Equal("b", rows[1]["item2"].AsString());
    }

    [Fact]
    public async Task Unnest_EmptyArray_YieldsNoRows()
    {
        DataValue json = DataValue.FromJsonValue("[]");
        List<Row> rows = await CollectRows([json]);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_EmptyVector_YieldsNoRows()
    {
        DataValue vector = DataValue.FromVector([]);
        List<Row> rows = await CollectRows([vector]);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_NullInput_YieldsNoRows()
    {
        DataValue nullValue = DataValue.Null(DataKind.Vector);
        List<Row> rows = await CollectRows([nullValue]);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unnest_NotAnArray_Throws()
    {
        DataValue json = DataValue.FromJsonValue("{\"key\": 1}");
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectRows([json]));
    }

    [Fact]
    public async Task Unnest_WrongArgCount_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectRows([]));
    }

    [Fact]
    public async Task Unnest_UnsupportedKind_Throws()
    {
        DataValue scalar = DataValue.FromFloat32(42);
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectRows([scalar]));
    }

    [Fact]
    public async Task Unnest_JsonNumericArray()
    {
        DataValue json = DataValue.FromJsonValue("[1.5, 2.5, 3.5]");
        List<Row> rows = await CollectRows([json]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1.5f, rows[0]["value"].AsFloat32(), 0.001f);
        Assert.Equal(2.5f, rows[1]["value"].AsFloat32(), 0.001f);
        Assert.Equal(3.5f, rows[2]["value"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task Unnest_JsonBooleanValues()
    {
        DataValue json = DataValue.FromJsonValue("[true, false]");
        List<Row> rows = await CollectRows([json]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1.0f, rows[0]["value"].AsFloat32());
        Assert.Equal(0.0f, rows[1]["value"].AsFloat32());
    }

    [Fact]
    public async Task Unnest_Cancellation_Stops()
    {
        DataValue vector = DataValue.FromVector(new float[1000]);
        CancellationTokenSource cancellationTokenSource = new();
        List<Row> rows = [];

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (Row row in _function.ExecuteAsync([vector], cancellationTokenSource.Token))
            {
                rows.Add(row);
                if (rows.Count == 5)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        });

        Assert.Equal(5, rows.Count);
    }

    private async Task<List<Row>> CollectRows(DataValue[] arguments)
    {
        List<Row> rows = [];
        await foreach (Row row in _function.ExecuteAsync(arguments, CancellationToken.None))
        {
            rows.Add(row);
        }
        return rows;
    }
}
