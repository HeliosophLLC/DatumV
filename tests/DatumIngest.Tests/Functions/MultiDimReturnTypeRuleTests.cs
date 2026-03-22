using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Verifies that <see cref="ReturnTypeRule.MultiDimArrayOf"/> declares its
/// result as a multi-dim array on the signature level, and that
/// <see cref="ExpressionTypeResolver.ResolveTypeShape"/> propagates
/// <c>IsMultiDim = true</c> for function calls whose matched variant uses it.
/// </summary>
public sealed class MultiDimReturnTypeRuleTests : ServiceTestBase
{
    [Fact]
    public void MultiDimArrayOf_ProducesArrayAndMultiDim()
    {
        ReturnTypeRule rule = ReturnTypeRule.MultiDimArrayOf(ReturnTypeRule.Constant(DataKind.Float32));

        Assert.True(rule.ProducesArray);
        Assert.True(rule.ProducesMultiDimArray);
        Assert.Equal(DataKind.Float32, rule.Resolve([]));
        Assert.Contains("MultiDimArray", rule.Describe());
    }

    [Fact]
    public void ArrayOf_DoesNotProduceMultiDim()
    {
        // Regression: ArrayOf must keep ProducesMultiDimArray = false so existing
        // flat-array-returning functions don't accidentally claim multi-dim output.
        ReturnTypeRule rule = ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32));

        Assert.True(rule.ProducesArray);
        Assert.False(rule.ProducesMultiDimArray);
    }

    [Fact]
    public void Constant_DoesNotProduceMultiDim()
    {
        ReturnTypeRule rule = ReturnTypeRule.Constant(DataKind.Float32);

        Assert.False(rule.ProducesArray);
        Assert.False(rule.ProducesMultiDimArray);
    }

    [Fact]
    public void Resolver_PropagatesIsMultiDimFromMatchedVariant()
    {
        // Build a function registry with a fake scalar function whose only signature
        // returns MultiDimArrayOf(Float32). The type resolver should report
        // IsMultiDim = true for a call to it.
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        registry.RegisterScalar<FakeMultiDimReturnFunction>();

        FunctionCallExpression call = new("fake_multidim_return", []);
        var shape = ExpressionTypeResolver.ResolveTypeShape(call, new Schema([new ColumnInfo("dummy", DataKind.Int32, nullable: true)]), registry);

        Assert.NotNull(shape);
        Assert.Equal(DataKind.Float32, shape.Value.Kind);
        Assert.True(shape.Value.IsArray);
        Assert.True(shape.Value.IsMultiDim);
    }

    [Fact]
    public void Resolver_FlatArrayFunction_LeavesIsMultiDimFalse()
    {
        // Sanity: ArrayOf-returning functions resolve with IsMultiDim=false.
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        registry.RegisterScalar<FakeFlatArrayReturnFunction>();

        FunctionCallExpression call = new("fake_flat_array_return", []);
        var shape = ExpressionTypeResolver.ResolveTypeShape(call, new Schema([new ColumnInfo("dummy", DataKind.Int32, nullable: true)]), registry);

        Assert.NotNull(shape);
        Assert.True(shape.Value.IsArray);
        Assert.False(shape.Value.IsMultiDim);
    }

    // ────────────────────────── Stubs ──────────────────────────

    private sealed class FakeMultiDimReturnFunction : IFunction, IScalarFunction
    {
        public static string Name => "fake_multidim_return";
        public static FunctionCategory Category => FunctionCategory.Array;
        public static string Description => "test-only stub returning a multi-dim array";
        public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        [
            new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.MultiDimArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
        ];

        public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
            FunctionMetadata.Validate<FakeMultiDimReturnFunction>(argumentKinds);

        public ValueTask<ValueRef> ExecuteAsync(
            ReadOnlyMemory<ValueRef> arguments,
            EvaluationFrame frame,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("type-resolution-only stub");
    }

    private sealed class FakeFlatArrayReturnFunction : IFunction, IScalarFunction
    {
        public static string Name => "fake_flat_array_return";
        public static FunctionCategory Category => FunctionCategory.Array;
        public static string Description => "test-only stub returning a flat 1-D array";
        public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        [
            new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
        ];

        public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
            FunctionMetadata.Validate<FakeFlatArrayReturnFunction>(argumentKinds);

        public ValueTask<ValueRef> ExecuteAsync(
            ReadOnlyMemory<ValueRef> arguments,
            EvaluationFrame frame,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException("type-resolution-only stub");
    }
}
