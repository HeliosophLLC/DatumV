using System.IO.Hashing;
using DatumIngest.Functions.Image;
using DatumIngest.Model;

namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// <see cref="MediaKindHandler"/> for image bags: JPEG, PNG, WebP, GIF entries.
/// Emits the canonical image schema (file_name, file, file_width, file_height,
/// file_channels, file_byte_length, file_orientation), parses dimensions inline
/// via <see cref="ImageHeaderParser"/>, and stamps a 32-bit content hash for
/// cross-arena Equals short-circuit.
/// </summary>
internal sealed class ImageKindHandler : MediaKindHandler
{
    public static readonly ImageKindHandler Instance = new();
    private ImageKindHandler() { }

    public override DataKind Kind => DataKind.Image;

    public override string[] ColumnNames { get; } =
    [
        "file_name",
        "file",
        "file_width",
        "file_height",
        "file_channels",
        "file_byte_length",
        "file_orientation",
    ];

    public override bool MatchesMagic(ReadOnlySpan<byte> magic)
    {
        if (magic.Length >= 3 && magic[0] == 0xFF && magic[1] == 0xD8 && magic[2] == 0xFF)
            return true; // JPEG
        if (magic.Length >= 8
            && magic[0] == 0x89 && magic[1] == 0x50 && magic[2] == 0x4E && magic[3] == 0x47
            && magic[4] == 0x0D && magic[5] == 0x0A && magic[6] == 0x1A && magic[7] == 0x0A)
            return true; // PNG
        if (magic.Length >= 12
            && magic[0] == (byte)'R' && magic[1] == (byte)'I' && magic[2] == (byte)'F' && magic[3] == (byte)'F'
            && magic[8] == (byte)'W' && magic[9] == (byte)'E' && magic[10] == (byte)'B' && magic[11] == (byte)'P')
            return true; // WebP
        if (magic.Length >= 6
            && magic[0] == (byte)'G' && magic[1] == (byte)'I' && magic[2] == (byte)'F' && magic[3] == (byte)'8'
            && (magic[4] == (byte)'7' || magic[4] == (byte)'9') && magic[5] == (byte)'a')
            return true; // GIF
        return false;
    }

    public override void PopulateRowFromArena(
        DataValue[] values, string fullName,
        long arenaOffset, int actualLength,
        ReadOnlySpan<byte> bytes,
        Arena arena)
    {
        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(bytes);
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(bytes));

        values[0] = DataValue.FromString(fullName, arena);
        values[1] = ImageDataValueFactory.FromArenaOffset(arenaOffset, actualLength, dims, hash32);
        PopulateDerived(values, dims, actualLength, arena);
    }

    public override void PopulateRowFromSidecar(
        DataValue[] values, string fullName,
        long sidecarOffset, long sidecarLength,
        ReadOnlySpan<byte> bytes,
        Arena arena)
    {
        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(bytes);
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(bytes));

        values[0] = DataValue.FromString(fullName, arena);
        values[1] = ImageDataValueFactory.FromSidecar(sidecarOffset, sidecarLength, dims, hash32);
        PopulateDerived(values, dims, sidecarLength, arena);
    }

    private static void PopulateDerived(DataValue[] values, ImageDimensions? dims, long byteLength, Arena arena)
    {
        values[5] = DataValue.FromInt64(byteLength);

        if (dims is null)
        {
            values[2] = DataValue.Null(DataKind.Int32);
            values[3] = DataValue.Null(DataKind.Int32);
            values[4] = DataValue.Null(DataKind.UInt8);
            values[6] = DataValue.Null(DataKind.String);
            return;
        }

        values[2] = DataValue.FromInt32(dims.Width);
        values[3] = DataValue.FromInt32(dims.Height);
        values[4] = DataValue.FromUInt8((byte)Math.Clamp(dims.Channels, 0, 255));

        string orientation = dims.Width > dims.Height ? "landscape"
                           : dims.Height > dims.Width ? "portrait"
                           : "square";
        values[6] = DataValue.FromString(orientation, arena);
    }
}
