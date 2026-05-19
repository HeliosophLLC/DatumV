using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Tests for <see cref="TypeofFunction"/> after the rich-type rewrite. Checks
/// that <c>typeof()</c> carries the runtime <see cref="TypeRegistry"/> id for
/// structs (so field names survive into rendering), returns null for null
/// inputs, and refuses to silently produce <c>f0..fN</c> output by throwing on
/// unplanned (TypeId=0) struct inputs.
/// </summary>
public sealed class TypeofFunctionTests : ServiceTestBase
{
    private static readonly TypeofFunction Func = new();

    private ValueRef Invoke(ValueRef arg) =>
        Func.ExecuteAsync(new ReadOnlyMemory<ValueRef>([arg]), CreateEvaluationFrame(), default)
            .GetAwaiter().GetResult();

    [Fact]
    public void Typeof_NullArgument_ReturnsNullTypeValue()
    {
        ValueRef result = Invoke(ValueRef.Null(DataKind.String));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Type, result.Kind);
    }

    [Fact]
    public void Typeof_Scalar_ReturnsKindWithZeroTypeId()
    {
        ValueRef result = Invoke(ValueRef.FromInt32(42));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(DataKind.Int32, result.AsType());
        Assert.Equal((ushort)0, result.TypeId);
    }

    [Fact]
    public void Typeof_String_ReturnsStringKind()
    {
        ValueRef result = Invoke(ValueRef.FromString("hello"));
        Assert.Equal(DataKind.String, result.AsType());
        Assert.Equal((ushort)0, result.TypeId);
    }

    [Fact]
    public void Typeof_StructWithTypeId_PropagatesTypeId()
    {
        // The TypeId rides on the inline placeholder of a managed-side struct
        // ValueRef (the same shape models produce via FromStruct + scatter).
        // typeof() must round-trip it so downstream formatters can recover
        // the field names without going through the arena.
        TypeRegistry registry = new();
        int structTypeId = registry.InternStructType(
        [
            new StructFieldDescriptor("score", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("label", registry.InternScalarType(DataKind.String)),
        ]);

        ValueRef arg = ValueRef.FromStruct(
            new[] { ValueRef.FromFloat32(0.9f), ValueRef.FromString("cat") },
            (ushort)structTypeId);

        ValueRef result = Invoke(arg);

        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(DataKind.Struct, result.AsType());
        Assert.Equal((ushort)structTypeId, result.TypeId);
    }

    [Fact]
    public void Typeof_StructWithoutTypeId_Throws()
    {
        // A managed-side struct ValueRef with TypeId=0 — the symptom we want
        // to catch. Any production path that produces such a value is a
        // missing plumbing site; typeof() should fail loudly so the gap gets
        // fixed at construction, not silently rendered as "Struct".
        ValueRef unplanned = ValueRef.FromStruct(
            new[] { ValueRef.FromInt32(1) });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Invoke(unplanned));
        Assert.Contains("no registered type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatType_StructDescriptor_RendersFieldNames()
    {
        TypeRegistry registry = new();
        int structTypeId = registry.InternStructType(
        [
            new StructFieldDescriptor("label", registry.InternScalarType(DataKind.String)),
            new StructFieldDescriptor("score", registry.InternScalarType(DataKind.Float32)),
        ]);
        DataValue typeValue = DataValue.FromType(DataKind.Struct, (ushort)structTypeId);

        string rendered = typeValue.FormatType(registry);
        Assert.Equal("Struct{label: String, score: Float32}", rendered);
    }

    [Fact]
    public void FormatType_ArrayOfStruct_RendersRecursively()
    {
        TypeRegistry registry = new();
        int structTypeId = registry.InternStructType(
        [
            new StructFieldDescriptor("kx", registry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("ky", registry.InternScalarType(DataKind.Float32)),
        ]);
        int arrayTypeId = registry.InternArrayType(DataKind.Struct, structTypeId);
        DataValue typeValue = DataValue.FromType(DataKind.Struct, (ushort)arrayTypeId);

        string rendered = typeValue.FormatType(registry);
        Assert.Equal("Array<Struct{kx: Float32, ky: Float32}>", rendered);
    }

    [Fact]
    public void FormatType_NoRegistry_DegradesToKindName()
    {
        DataValue typeValue = DataValue.FromType(DataKind.Struct, typeId: 5);
        Assert.Equal("Struct", typeValue.FormatType(null));
    }

    [Fact]
    public void FormatType_ScalarTag_ReturnsKindName()
    {
        DataValue typeValue = DataValue.FromType(DataKind.Float32);
        Assert.Equal("Float32", typeValue.FormatType());
    }
}
