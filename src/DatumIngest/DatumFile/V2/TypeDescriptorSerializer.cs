using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Self-contained binary codec for <see cref="TypeDescriptor"/> blobs that
/// live in the sidecar and are addressed by entries in the file footer's
/// type table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Self-contained per blob.</strong> Each descriptor blob is a
/// complete recursive serialization — field types and element types are
/// inlined, not referenced by id. This decouples the blob format from the
/// type table: the reader can deserialize any blob in isolation, and on-disk
/// type-ids never appear inside a blob (they're directory-only). The cost
/// is that two descriptors with shared subtypes (e.g. two <c>Struct</c>s
/// each with an <c>Int32</c> field) duplicate the primitive subtree's ~3
/// bytes per occurrence — negligible compared to keeping a topological-order
/// invariant and cross-blob references straight.
/// </para>
/// <para>
/// Dedup happens at intern time: <see cref="DeserializeAndIntern"/> walks
/// the blob bottom-up and the registry's <see cref="TypeRegistry"/> hash
/// table collapses identical subtrees onto one runtime id. Two on-disk
/// type-ids whose blobs describe the same shape resolve to the same runtime
/// id once interned.
/// </para>
/// </remarks>
internal static class TypeDescriptorSerializer
{
    /// <summary>
    /// Codec version. Bump when the wire format changes; readers reject
    /// unknown versions to fail loudly rather than mis-parse.
    /// </summary>
    private const byte BlobVersion = 1;

    [Flags]
    private enum DescriptorFlags : byte
    {
        None = 0,
        IsArray = 0x01,
        Nullable = 0x02,
        HasFields = 0x04,
        HasElementTypeId = 0x08,
    }

    /// <summary>
    /// Serializes <paramref name="descriptor"/> into a self-contained blob
    /// using <paramref name="registry"/> to resolve nested
    /// <see cref="StructFieldDescriptor.TypeId"/>s and
    /// <see cref="TypeDescriptor.ElementTypeId"/>s. The returned bytes
    /// carry the shape recursively and are independent of any other type-id
    /// in the registry.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A nested type-id referenced by the descriptor isn't registered in
    /// <paramref name="registry"/>. Fail-loud rather than write a partial blob.
    /// </exception>
    public static byte[] SerializeFromDescriptor(TypeDescriptor descriptor, TypeRegistry registry)
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        bw.Write(BlobVersion);
        WriteDescriptor(bw, descriptor, registry);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a blob produced by <see cref="SerializeFromDescriptor"/>
    /// and interns every nested shape into <paramref name="registry"/>,
    /// returning the runtime type-id for the outer descriptor.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The blob's version byte doesn't match <see cref="BlobVersion"/>, or
    /// the byte stream is truncated/malformed.
    /// </exception>
    public static int DeserializeAndIntern(ReadOnlySpan<byte> blob, TypeRegistry registry)
    {
        // ToArray is the simplest way to feed a span into BinaryReader without
        // pinning. Blobs are tens of bytes — copy cost is negligible.
        using MemoryStream ms = new(blob.ToArray(), writable: false);
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: false);
        byte version = br.ReadByte();
        if (version != BlobVersion)
        {
            throw new InvalidDataException(
                $"TypeDescriptor blob version {version} is not supported; expected {BlobVersion}.");
        }
        return ReadDescriptorAndIntern(br, registry);
    }

    private static void WriteDescriptor(BinaryWriter bw, TypeDescriptor descriptor, TypeRegistry registry)
    {
        DescriptorFlags flags = DescriptorFlags.None;
        if (descriptor.IsArray) flags |= DescriptorFlags.IsArray;
        if (descriptor.Nullable) flags |= DescriptorFlags.Nullable;
        if (descriptor.Fields is not null) flags |= DescriptorFlags.HasFields;
        if (descriptor.ElementTypeId is not null) flags |= DescriptorFlags.HasElementTypeId;

        bw.Write((byte)descriptor.Kind);
        bw.Write((byte)flags);

        if (descriptor.Fields is { } fields)
        {
            // ushort field count — 65k fields is comically more than any real shape;
            // the cap is implicit in the type. The field's TypeId is inlined as a
            // recursive descriptor (no on-disk id reference) so the blob is
            // self-contained and the on-disk type table acts as a directory only.
            bw.Write(checked((ushort)fields.Count));
            foreach (StructFieldDescriptor field in fields)
            {
                bw.Write(field.Name);
                TypeDescriptor fieldType = ResolveOrThrow(field.TypeId, registry, $"struct field '{field.Name}'");
                WriteDescriptor(bw, fieldType, registry);
            }
        }

        if (descriptor.ElementTypeId is { } eid)
        {
            TypeDescriptor elementType = ResolveOrThrow(eid, registry, "array element");
            WriteDescriptor(bw, elementType, registry);
        }
    }

    private static int ReadDescriptorAndIntern(BinaryReader br, TypeRegistry registry)
    {
        DataKind kind = (DataKind)br.ReadByte();
        DescriptorFlags flags = (DescriptorFlags)br.ReadByte();

        IReadOnlyList<StructFieldDescriptor>? fields = null;
        if ((flags & DescriptorFlags.HasFields) != 0)
        {
            ushort fieldCount = br.ReadUInt16();
            StructFieldDescriptor[] fieldArray = new StructFieldDescriptor[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                string name = br.ReadString();
                int fieldTypeId = ReadDescriptorAndIntern(br, registry);
                fieldArray[i] = new StructFieldDescriptor(name, fieldTypeId);
            }
            fields = fieldArray;
        }

        int? elementTypeId = null;
        if ((flags & DescriptorFlags.HasElementTypeId) != 0)
        {
            elementTypeId = ReadDescriptorAndIntern(br, registry);
        }

        TypeDescriptor descriptor = new(
            kind,
            IsArray: (flags & DescriptorFlags.IsArray) != 0,
            Nullable: (flags & DescriptorFlags.Nullable) != 0,
            Fields: fields,
            ElementTypeId: elementTypeId);

        return registry.InternDescriptor(descriptor);
    }

    private static TypeDescriptor ResolveOrThrow(int typeId, TypeRegistry registry, string context)
    {
        TypeDescriptor? desc;
        try
        {
            desc = registry.GetDescriptor(typeId);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Wrap the registry's "id not in range" with a context-rich
            // message so the writer knows *which* descriptor referenced
            // the missing id rather than just "id 999 not registered".
            throw new InvalidOperationException(
                $"TypeDescriptorSerializer: type-id {typeId} ({context}) is not registered. " +
                "Every type-id reachable from a serialized descriptor must be present in the " +
                "writer's TypeRegistry.", ex);
        }
        if (desc is null)
        {
            throw new InvalidOperationException(
                $"TypeDescriptorSerializer: type-id {typeId} ({context}) resolved to NoType. " +
                "Cannot serialize a sentinel type-id.");
        }
        return desc;
    }
}
