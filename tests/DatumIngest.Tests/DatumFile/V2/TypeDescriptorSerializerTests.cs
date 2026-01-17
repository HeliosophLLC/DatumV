using DatumIngest.DatumFile.V2;
using DatumIngest.Model;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// Round-trip tests for the descriptor blob codec. The codec is the
/// foundation of the file's type table — bugs here drop schema info on
/// every read. Tests pin the recursive shape (struct fields, nested
/// arrays of structs) and the cross-registry intern-dedup behaviour.
/// </summary>
public sealed class TypeDescriptorSerializerTests
{
    [Fact]
    public void RoundTrip_FlatStruct_PreservesFieldNamesAndKinds()
    {
        TypeRegistry write = new();
        int typeId = write.InternStructType(
        [
            new StructFieldDescriptor("label", write.InternScalarType(DataKind.String)),
            new StructFieldDescriptor("score", write.InternScalarType(DataKind.Float32)),
        ]);

        byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(
            write.GetDescriptor(typeId)!, write);

        TypeRegistry read = new();
        int restoredId = TypeDescriptorSerializer.DeserializeAndIntern(blob, read);
        TypeDescriptor restored = read.GetDescriptor(restoredId)!;

        Assert.Equal(DataKind.Struct, restored.Kind);
        Assert.False(restored.IsArray);
        Assert.NotNull(restored.Fields);
        Assert.Equal(2, restored.Fields!.Count);
        Assert.Equal("label", restored.Fields[0].Name);
        Assert.Equal("score", restored.Fields[1].Name);

        TypeDescriptor labelField = read.GetDescriptor(restored.Fields[0].TypeId)!;
        Assert.Equal(DataKind.String, labelField.Kind);
        TypeDescriptor scoreField = read.GetDescriptor(restored.Fields[1].TypeId)!;
        Assert.Equal(DataKind.Float32, scoreField.Kind);
    }

    [Fact]
    public void RoundTrip_NestedStruct_PreservesShape()
    {
        // Struct{outer: Struct{inner: Float32}} — round trips through one
        // recursive call into ReadDescriptorAndIntern per nesting level.
        TypeRegistry write = new();
        int innerId = write.InternStructType(
        [
            new StructFieldDescriptor("inner", write.InternScalarType(DataKind.Float32)),
        ]);
        int outerId = write.InternStructType(
        [
            new StructFieldDescriptor("outer", innerId),
        ]);

        byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(
            write.GetDescriptor(outerId)!, write);

        TypeRegistry read = new();
        int restoredOuterId = TypeDescriptorSerializer.DeserializeAndIntern(blob, read);
        TypeDescriptor restored = read.GetDescriptor(restoredOuterId)!;

        Assert.Equal("outer", restored.Fields![0].Name);
        TypeDescriptor restoredInner = read.GetDescriptor(restored.Fields[0].TypeId)!;
        Assert.Equal("inner", restoredInner.Fields![0].Name);
        TypeDescriptor restoredFloat = read.GetDescriptor(restoredInner.Fields[0].TypeId)!;
        Assert.Equal(DataKind.Float32, restoredFloat.Kind);
    }

    [Fact]
    public void RoundTrip_ArrayOfStruct_PreservesElementTypeId()
    {
        // Array<Struct{label, score}> — the array descriptor inlines the
        // element shape recursively; on read, the element is interned first
        // and its returned id populates ElementTypeId.
        TypeRegistry write = new();
        int elementId = write.InternStructType(
        [
            new StructFieldDescriptor("label", write.InternScalarType(DataKind.String)),
            new StructFieldDescriptor("score", write.InternScalarType(DataKind.Float32)),
        ]);
        int arrayId = write.InternArrayType(DataKind.Struct, elementTypeId: elementId);

        byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(
            write.GetDescriptor(arrayId)!, write);

        TypeRegistry read = new();
        int restoredArrayId = TypeDescriptorSerializer.DeserializeAndIntern(blob, read);
        TypeDescriptor restored = read.GetDescriptor(restoredArrayId)!;

        Assert.Equal(DataKind.Struct, restored.Kind);
        Assert.True(restored.IsArray);
        Assert.NotNull(restored.ElementTypeId);

        TypeDescriptor restoredElement = read.GetDescriptor(restored.ElementTypeId!.Value)!;
        Assert.Equal(DataKind.Struct, restoredElement.Kind);
        Assert.False(restoredElement.IsArray);
        Assert.Equal(2, restoredElement.Fields!.Count);
        Assert.Equal("label", restoredElement.Fields[0].Name);
    }

    [Fact]
    public void DeserializeAndIntern_DupShape_ReusesRuntimeId()
    {
        // Two on-disk blobs for the same shape must dedup at intern time —
        // two on-disk type-ids → one runtime id. This is the cross-file
        // dedup property the registry's Dictionary<TypeDescriptor, int>
        // gives us for free.
        TypeRegistry write = new();
        int typeId = write.InternStructType(
        [
            new StructFieldDescriptor("x", write.InternScalarType(DataKind.Int32)),
        ]);

        byte[] blob = TypeDescriptorSerializer.SerializeFromDescriptor(
            write.GetDescriptor(typeId)!, write);

        TypeRegistry read = new();
        int firstRuntimeId = TypeDescriptorSerializer.DeserializeAndIntern(blob, read);
        int secondRuntimeId = TypeDescriptorSerializer.DeserializeAndIntern(blob, read);

        Assert.Equal(firstRuntimeId, secondRuntimeId);
    }

    [Fact]
    public void Deserialize_UnsupportedVersion_Throws()
    {
        // Future codec versions must fail loudly rather than silently
        // mis-parse — corrupt schema would render every struct in the file
        // as f0..fN, which is the exact thing this PR fixes.
        byte[] forged = [0xFF /* unknown version */, (byte)DataKind.Int32, 0x00];

        Assert.Throws<InvalidDataException>(() =>
            TypeDescriptorSerializer.DeserializeAndIntern(forged, new TypeRegistry()));
    }

    [Fact]
    public void Serialize_UnregisteredFieldTypeId_Throws()
    {
        // A descriptor whose nested TypeId isn't in the registry has to fail
        // at serialize time — writing a partial blob would leave the file
        // pointing at an unresolvable shape.
        TypeRegistry registry = new();
        TypeDescriptor brokenStruct = new(
            DataKind.Struct, IsArray: false, Nullable: false,
            Fields: [new StructFieldDescriptor("x", TypeId: 999)],  // no such id
            ElementTypeId: null);

        Assert.Throws<InvalidOperationException>(() =>
            TypeDescriptorSerializer.SerializeFromDescriptor(brokenStruct, registry));
    }
}
