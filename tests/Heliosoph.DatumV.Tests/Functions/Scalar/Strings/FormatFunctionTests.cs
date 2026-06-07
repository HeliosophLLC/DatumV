using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="FormatFunction"/> — sprintf-style formatting with
/// the PG-flavoured <c>%s</c>, <c>%I</c>, <c>%L</c>, <c>%%</c>, and
/// positional <c>%n$type</c> conversions.
/// </summary>
public sealed class FormatFunctionTests
{
    private static async Task<string> Run(params ValueRef[] args)
    {
        FormatFunction function = new();
        ValueRef result = await function.ExecuteAsync(args, default, default);
        return result.AsString();
    }

    [Fact]
    public async Task Format_SimpleString()
    {
        string result = await Run(
            ValueRef.FromString("Hello %s!"),
            ValueRef.FromString("World"));
        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public async Task Format_Positional()
    {
        string result = await Run(
            ValueRef.FromString("Hello %s, %1$s"),
            ValueRef.FromString("World"));
        Assert.Equal("Hello World, World", result);
    }

    [Fact]
    public async Task Format_LiteralPercent()
    {
        string result = await Run(
            ValueRef.FromString("50%% off"));
        Assert.Equal("50% off", result);
    }

    [Fact]
    public async Task Format_IdentifierAndLiteralQuoting()
    {
        string result = await Run(
            ValueRef.FromString("INSERT INTO %I VALUES (%L)"),
            ValueRef.FromString("my_table"),
            ValueRef.FromString("O'Reilly"));
        Assert.Equal("INSERT INTO my_table VALUES ('O''Reilly')", result);
    }

    [Fact]
    public async Task Format_NullString_RendersAsEmptyForS()
    {
        string result = await Run(
            ValueRef.FromString("[%s]"),
            ValueRef.Null(DataKind.String));
        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task Format_NullForL_RendersAsBareNULL()
    {
        string result = await Run(
            ValueRef.FromString("VALUES (%L)"),
            ValueRef.Null(DataKind.String));
        Assert.Equal("VALUES (NULL)", result);
    }

    [Fact]
    public async Task Format_NullForI_Throws()
    {
        FormatFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("INTO %I"), ValueRef.Null(DataKind.String) }, default, default));
    }

    [Fact]
    public async Task Format_NonStringArgs_RenderViaToString()
    {
        string result = await Run(
            ValueRef.FromString("%s=%s"),
            ValueRef.FromString("answer"),
            ValueRef.FromInt32(42));
        Assert.Equal("answer=42", result);
    }

    [Fact]
    public async Task Format_UnknownConversion_Throws()
    {
        FormatFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("hi %d"), ValueRef.FromInt32(1) }, default, default));
    }

    [Fact]
    public void Format_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<FormatFunction>(registry.TryGetScalar("format"));
    }
}
