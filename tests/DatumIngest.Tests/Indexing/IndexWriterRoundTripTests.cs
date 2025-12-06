using DatumIngest.Indexing;
using DatumIngest.IO;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Round-trip tests for <see cref="IndexWriter.WriteDataValue"/> and
/// <see cref="IndexReader.ReadDataValue"/>. Iterates every <see cref="DataKind"/>
/// value so that adding a new kind without updating the serializers produces
/// a test failure rather than a silent runtime crash.
/// </summary>
public sealed class IndexWriterRoundTripTests : ServiceTestBase
{
    /// <summary>
    /// Kinds that are intentionally unsupported by the index serializer because
    /// they are composite types with recursive structure.
    /// </summary>
    private static readonly HashSet<DataKind> UnsupportedKinds =
    [
        DataKind.Unknown,
        DataKind.Struct,
        DataKind.Audio,
        DataKind.Video,
        DataKind.Json,
    ];

    /// <summary>
    /// Every supported <see cref="DataKind"/> must survive a write→read round-trip
    /// through <see cref="IndexWriter.WriteDataValue(BinaryWriter, DataValue)"/> and
    /// <see cref="IndexReader.ReadDataValue(BinaryReader)"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSupportedKinds))]
    public void WriteDataValue_BinaryWriter_RoundTrips(DataKind kind)
    {
        DataValue original = CreateSampleValue(kind);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        DataValueWriter.WriteDataValue(writer, original);
        writer.Flush();

        stream.Position = 0;
        using BinaryReader reader = new(stream);
        DataValue restored = DataValueReader.ReadDataValue(reader);

        AssertValuesEqual(original, restored, kind);
    }

    /// <summary>
    /// Same round-trip through the <see cref="BufferedWriter"/> overload.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSupportedKinds))]
    public void WriteDataValue_BufferedWriter_RoundTrips(DataKind kind)
    {
        DataValue original = CreateSampleValue(kind);

        using MemoryStream stream = new();
        using (BufferedWriter buffered = new(stream))
        {
            DataValueWriter.WriteDataValue(buffered, original);
        }

        stream.Position = 0;
        using BinaryReader reader = new(stream);
        DataValue restored = DataValueReader.ReadDataValue(reader);

        AssertValuesEqual(original, restored, kind);
    }

    /// <summary>
    /// Unsupported kinds must throw <see cref="NotSupportedException"/> rather than
    /// silently producing corrupt data.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllUnsupportedKinds))]
    public void WriteDataValue_UnsupportedKind_Throws(DataKind kind)
    {
        DataValue value = CreateSampleValue(kind);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Assert.Throws<NotSupportedException>(() => DataValueWriter.WriteDataValue(writer, value));
    }

    /// <summary>
    /// Guards against a new <see cref="DataKind"/> being added without updating
    /// this test. If this fails, add the new kind to either
    /// <see cref="CreateSampleValue"/> (and the serializer) or <see cref="UnsupportedKinds"/>.
    /// </summary>
    [Fact]
    public void AllDataKinds_AreCoveredByTests()
    {
        HashSet<DataKind> allKinds = new(Enum.GetValues<DataKind>());
        HashSet<DataKind> testedKinds = new(AllSupportedKinds().Concat(AllUnsupportedKinds())
            .Select(args => (DataKind)args[0]));

        HashSet<DataKind> missing = new(allKinds);
        missing.ExceptWith(testedKinds);

        Assert.True(missing.Count == 0,
            $"DataKind values not covered by IndexWriter round-trip tests: {string.Join(", ", missing)}. " +
            "Add them to CreateSampleValue or UnsupportedKinds.");
    }

    // ──────────────────── Test data ────────────────────

    public static IEnumerable<object[]> AllSupportedKinds() =>
        Enum.GetValues<DataKind>()
            .Where(k => !UnsupportedKinds.Contains(k))
            .Select(k => new object[] { k });

    public static IEnumerable<object[]> AllUnsupportedKinds() =>
        UnsupportedKinds.Select(k => new object[] { k });

    // ──────────────────── Sample value factory ────────────────────

    /// <summary>
    /// Creates a non-null sample <see cref="DataValue"/> for the given kind.
    /// When a new <see cref="DataKind"/> is added, this method must be extended
    /// to cover it — the <see cref="AllDataKinds_AreCoveredByTests"/> test will
    /// catch any omission.
    /// </summary>
    internal static DataValue CreateSampleValue(DataKind kind) => kind switch
    {
        DataKind.Unknown  => default,
        DataKind.Float32  => DataValue.FromFloat32(3.14f),
        DataKind.Float64  => DataValue.FromFloat64(2.718281828),
        DataKind.UInt8    => DataValue.FromUInt8(42),
        DataKind.Int8     => DataValue.FromInt8(-7),
        DataKind.Int16    => DataValue.FromInt16(-1234),
        DataKind.UInt16   => DataValue.FromUInt16(5678),
        DataKind.Int32    => DataValue.FromInt32(-100_000),
        DataKind.UInt32   => DataValue.FromUInt32(200_000),
        DataKind.Int64    => DataValue.FromInt64(-9_000_000_000L),
        DataKind.UInt64   => DataValue.FromUInt64(18_000_000_000UL),
        DataKind.Boolean  => DataValue.FromBoolean(true),
        DataKind.String   => DataValue.FromString("hello world"),
        DataKind.Date     => DataValue.FromDate(new DateOnly(2026, 4, 15)),
        DataKind.DateTime => DataValue.FromDateTime(
            new DateTimeOffset(2026, 4, 15, 10, 30, 0, TimeSpan.FromHours(-5))),
        DataKind.Time     => DataValue.FromTime(new TimeOnly(14, 30, 59, 123)),
        DataKind.Duration => DataValue.FromDuration(TimeSpan.FromSeconds(90.5)),
        DataKind.Uuid     => DataValue.FromUuid(Guid.Parse("12345678-1234-1234-1234-123456789abc")),
        DataKind.Image    => DataValue.FromImage([0xFF, 0xD8, 0xFF, 0xE0]),
        DataKind.Audio    => DataValue.FromAudio([0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45]),
        DataKind.Video    => DataValue.FromVideo([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D]),
        DataKind.Json     => DataValue.FromJson([0xA1, 0x61, 0x61, 0x01]),  // {"a":1} canonical CBOR
        DataKind.Struct   => DataValue.FromStruct(2, [DataValue.FromString("a"), DataValue.FromInt32(1)]),
        DataKind.Type     => DataValue.FromType(DataKind.Int32),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            $"No sample value defined for DataKind.{kind}. Update CreateSampleValue."),
    };

    // ──────────────────── Assertion helpers ────────────────────

    private static void AssertValuesEqual(DataValue expected, DataValue actual, DataKind kind)
    {
        Assert.Equal(kind, actual.Kind);

        switch (kind)
        {
            case DataKind.Float32:
                Assert.Equal(expected.AsFloat32(), actual.AsFloat32());
                break;
            case DataKind.Float64:
                Assert.Equal(expected.AsFloat64(), actual.AsFloat64());
                break;
            case DataKind.UInt8:
                Assert.Equal(expected.AsUInt8(), actual.AsUInt8());
                break;
            case DataKind.Int8:
                Assert.Equal(expected.AsInt8(), actual.AsInt8());
                break;
            case DataKind.Int16:
                Assert.Equal(expected.AsInt16(), actual.AsInt16());
                break;
            case DataKind.UInt16:
                Assert.Equal(expected.AsUInt16(), actual.AsUInt16());
                break;
            case DataKind.Int32:
                Assert.Equal(expected.AsInt32(), actual.AsInt32());
                break;
            case DataKind.UInt32:
                Assert.Equal(expected.AsUInt32(), actual.AsUInt32());
                break;
            case DataKind.Int64:
                Assert.Equal(expected.AsInt64(), actual.AsInt64());
                break;
            case DataKind.UInt64:
                Assert.Equal(expected.AsUInt64(), actual.AsUInt64());
                break;
            case DataKind.Boolean:
                Assert.Equal(expected.AsBoolean(), actual.AsBoolean());
                break;
            case DataKind.String:
                Assert.Equal(expected.AsString(), actual.AsString());
                break;
            case DataKind.Date:
                Assert.Equal(expected.AsDate(), actual.AsDate());
                break;
            case DataKind.DateTime:
                Assert.Equal(expected.AsDateTime(), actual.AsDateTime());
                break;
            case DataKind.Time:
                Assert.Equal(expected.AsTime(), actual.AsTime());
                break;
            case DataKind.Duration:
                Assert.Equal(expected.AsDuration(), actual.AsDuration());
                break;
            case DataKind.Uuid:
                Assert.Equal(expected.AsUuid(), actual.AsUuid());
                break;
            case DataKind.Image:
                Assert.Equal(expected.AsImage(), actual.AsImage());
                break;
            case DataKind.Type:
                Assert.Equal(expected.AsType(), actual.AsType());
                break;
            default:
                Assert.Fail($"No assertion defined for DataKind.{kind}.");
                break;
        }
    }
}
