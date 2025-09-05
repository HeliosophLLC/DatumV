using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Validates that <see cref="RowSerializer"/> rejects corrupted spill-file streams
/// with clean exceptions rather than undefined behavior or crashes.
/// </summary>
public sealed class RowSerializerCorruptionTests
{
    [Fact]
    public void ReadDataValue_UnknownDataKind_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(false); // isNull = false
        writer.Write((byte)0xFF); // Unknown DataKind.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Assert.Throws<InvalidDataException>(() => RowSerializer.ReadDataValue(reader));
    }

    [Fact]
    public void ReadDataValue_TruncatedFloat32Payload_Throws()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(false); // isNull = false
        writer.Write((byte)DataKind.Float32);
        // No float payload follows.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Assert.ThrowsAny<Exception>(() => RowSerializer.ReadDataValue(reader));
    }

    [Fact]
    public void ReadDataValue_TruncatedStringPayload_Throws()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(false); // isNull
        writer.Write((byte)DataKind.String);
        // BinaryWriter.Write(string) uses a 7-bit encoded length prefix.
        // Write a length prefix claiming 100 bytes but then end the stream.
        writer.Write((byte)100); // Length prefix.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Assert.ThrowsAny<Exception>(() => RowSerializer.ReadDataValue(reader));
    }

    [Fact]
    public void ReadSchema_EmptyStream_Throws()
    {
        using MemoryStream stream = new([]);
        using BinaryReader reader = new(stream);

        Assert.ThrowsAny<Exception>(() =>
            RowSerializer.ReadSchema(reader, out _, out _));
    }

    [Fact]
    public void ReadSchema_TruncatedColumnNames_Throws()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(5); // fieldCount = 5
        writer.Write("col0"); // Only write one name instead of five.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Assert.ThrowsAny<Exception>(() =>
            RowSerializer.ReadSchema(reader, out _, out _));
    }

    [Fact]
    public void ReadRow_TruncatedMidRow_Throws()
    {
        // Write a valid schema then only half a row.
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Schema: 2 columns.
        writer.Write(2);
        writer.Write("a");
        writer.Write("b");

        // Write first value only.
        writer.Write(false); // isNull
        writer.Write((byte)DataKind.Int32);
        writer.Write(42);
        // Second value missing.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);
        Assert.ThrowsAny<Exception>(() => RowSerializer.ReadRow(reader, names, nameIndex));
    }

    [Fact]
    public void ReadDataValue_NullWithUnknownKind_ReturnsNullValue()
    {
        // Even an unknown kind should produce a typed null when isNull=true,
        // or throw a clean exception.
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(true); // isNull = true
        writer.Write((byte)0xFE); // Unknown DataKind.
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);

        // This should either return a null DataValue or throw cleanly — not crash.
        try
        {
            DataValue value = RowSerializer.ReadDataValue(reader);
            Assert.True(value.IsNull);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            // Clean exception — acceptable.
        }
    }
}
