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
    /// Kinds that are intentionally unsupported by the no-store
    /// <see cref="DataValueWriter.WriteDataValue(BinaryWriter, DataValue)"/>
    /// overload. These either need an arena-backed payload (Image / Audio /
    /// Video / Json — bytes live in a value store), are recursive composites
    /// (Struct), are sentinel values (Unknown), or are runtime-only lazy
    /// handles that resolve through a per-query registry rather than an index
    /// (AudioFrame / VideoFrame). Production callers route these through the
    /// IValueStore-aware overload instead — the indexable = self-contained
    /// rule means an indexed key is never one of these.
    /// </summary>
    private static readonly HashSet<DataKind> UnsupportedKinds =
    [
        DataKind.Unknown,
        DataKind.Struct,
        DataKind.Audio,
        DataKind.AudioSlice,
        DataKind.Video,
        DataKind.VideoFrame,
        DataKind.VideoSlice,
        DataKind.Json,
        DataKind.Image,
        DataKind.PointCloud,
        DataKind.Mesh,
        // Row-scoped runtime-only kinds — `Drawing` and `Lambda` carry
        // managed payloads (DrawingPayload tree, LambdaValue closure) that
        // intentionally refuse persistence at the arena boundary, so
        // they're never indexable. `Color` is a 32-bit inline value but
        // only ever appears as a transient ingredient to a Drawing,
        // never as a stored column.
        DataKind.Color,
        DataKind.Drawing,
        DataKind.Lambda,
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
        DataKind.Float16  => DataValue.FromFloat16((Half)1.5),
        DataKind.Float32  => DataValue.FromFloat32(3.14f),
        DataKind.Float64  => DataValue.FromFloat64(2.718281828),
        DataKind.Decimal  => DataValue.FromDecimal(123.456m),
        DataKind.UInt8    => DataValue.FromUInt8(42),
        DataKind.Int8     => DataValue.FromInt8(-7),
        DataKind.Int16    => DataValue.FromInt16(-1234),
        DataKind.UInt16   => DataValue.FromUInt16(5678),
        DataKind.Int32    => DataValue.FromInt32(-100_000),
        DataKind.UInt32   => DataValue.FromUInt32(200_000),
        DataKind.Int64    => DataValue.FromInt64(-9_000_000_000L),
        DataKind.UInt64   => DataValue.FromUInt64(18_000_000_000UL),
        DataKind.Int128   => DataValue.FromInt128((Int128)(-1_000_000_000_000L)),
        DataKind.UInt128  => DataValue.FromUInt128((UInt128)2_000_000_000_000UL),
        DataKind.Boolean  => DataValue.FromBoolean(true),
        DataKind.String   => DataValue.FromString("hello world"),
        DataKind.Date     => DataValue.FromDate(new DateOnly(2026, 4, 15)),
        DataKind.TimestampTz => DataValue.FromTimestampTz(
            new DateTimeOffset(2026, 4, 15, 10, 30, 0, TimeSpan.FromHours(-5))),
        DataKind.Timestamp => DataValue.FromTimestamp(
            new DateTime(2026, 4, 15, 10, 30, 0, DateTimeKind.Unspecified)),
        DataKind.Time     => DataValue.FromTime(new TimeOnly(14, 30, 59, 123)),
        DataKind.Duration => DataValue.FromDuration(TimeSpan.FromSeconds(90.5)),
        DataKind.Uuid     => DataValue.FromUuid(Guid.Parse("12345678-1234-1234-1234-123456789abc")),
        // Sidecar pointers carry no payload; cheap test-side construction
        // for the unsupported-kinds path (we only need a DataValue with
        // the right Kind to check that WriteDataValue throws).
        DataKind.Image    => DataValue.FromImageInSidecar(offset: 0, length: 4),
        DataKind.Audio    => DataValue.FromAudioInSidecar(offset: 0, length: 12),
        DataKind.Video    => DataValue.FromVideoInSidecar(offset: 0, length: 12),
        // Runtime-only handles never round-trip through index writers; the
        // null-with-kind variant is enough to exercise the unsupported-kind
        // throw path below.
        DataKind.AudioSlice => DataValue.Null(DataKind.AudioSlice),
        DataKind.VideoFrame => DataValue.Null(DataKind.VideoFrame),
        DataKind.VideoSlice => DataValue.Null(DataKind.VideoSlice),
        DataKind.Json     => DataValue.FromJsonInSidecar(offset: 0, length: 4),
        DataKind.PointCloud => DataValue.FromPointCloudInSidecar(offset: 0, length: 40),
        DataKind.Mesh => DataValue.FromMeshInSidecar(offset: 0, length: 48),
        DataKind.Struct   => DataValue.NullUntypedStruct(),
        DataKind.Type     => DataValue.FromType(DataKind.Int32),
        DataKind.Point2D  => DataValue.FromPoint2D(4, 5),
        DataKind.Point3D  => DataValue.FromPoint3D(1, 2, 3),
        // Drawing / Lambda / Color are runtime-only or transient and don't
        // persist (Drawing + Lambda refuse arena writes; Color appears only
        // as a Drawing-tree ingredient). The unsupported-kind path just
        // needs a DataValue with the right Kind to check WriteDataValue
        // throws — a null-with-kind suffices.
        DataKind.Drawing  => DataValue.Null(DataKind.Drawing),
        DataKind.Lambda   => DataValue.Null(DataKind.Lambda),
        DataKind.Color    => DataValue.Null(DataKind.Color),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            $"No sample value defined for DataKind.{kind}. Update CreateSampleValue."),
    };

    // ──────────────────── Assertion helpers ────────────────────

    private static void AssertValuesEqual(DataValue expected, DataValue actual, DataKind kind)
    {
        Assert.Equal(kind, actual.Kind);

        switch (kind)
        {
            case DataKind.Float16:
                Assert.Equal(expected.AsFloat16(), actual.AsFloat16());
                break;
            case DataKind.Float32:
                Assert.Equal(expected.AsFloat32(), actual.AsFloat32());
                break;
            case DataKind.Float64:
                Assert.Equal(expected.AsFloat64(), actual.AsFloat64());
                break;
            case DataKind.Decimal:
                Assert.Equal(expected.AsDecimal(), actual.AsDecimal());
                break;
            case DataKind.Int128:
                Assert.Equal(expected.AsInt128(), actual.AsInt128());
                break;
            case DataKind.UInt128:
                Assert.Equal(expected.AsUInt128(), actual.AsUInt128());
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
            case DataKind.TimestampTz:
                Assert.Equal(expected.AsTimestampTz(), actual.AsTimestampTz());
                break;
            case DataKind.Timestamp:
                Assert.Equal(expected.AsTimestamp(), actual.AsTimestamp());
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
            case DataKind.Type:
                Assert.Equal(expected.AsType(), actual.AsType());
                break;
            case DataKind.Point2D:
                Assert.Equal(expected.AsPoint2D(), actual.AsPoint2D());
                break;
            case DataKind.Point3D:
                Assert.Equal(expected.AsPoint3D(), actual.AsPoint3D());
                break;
            default:
                Assert.Fail($"No assertion defined for DataKind.{kind}.");
                break;
        }
    }
}
