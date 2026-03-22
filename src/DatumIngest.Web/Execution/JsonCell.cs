namespace DatumIngest.Web.Execution;

internal sealed record JsonCell(
    string Kind,
    string? Text = null,
    string? Mime = null,
    string? DataB64 = null,
    // Populated for kind="media_array" — one entry per element. Single-blob
    // media still uses Mime + DataB64.
    IReadOnlyList<JsonMediaItem>? Items = null,
    // Transport encoding for binary payloads carried in DataB64. Null = raw
    // (legacy Image/Audio/Video — already-compressed codec bytes); "gzip" =
    // base64 of gzip-compressed payload, decompress in the client.
    string? Encoding = null,
    // Populated for kind="pointcloud". Lets the front-end show a
    // metadata-only summary in the row grid without decoding the blob, and
    // gives the 3D viewer the dimensions/flags it needs to set up
    // BufferAttributes without re-parsing the header.
    JsonPointCloudInfo? PointCloud = null);

internal sealed record JsonMediaItem(string Mime, string DataB64);

internal sealed record JsonPointCloudInfo(
    int PointCount,
    bool HasColor,
    int Width,
    int Height,
    string CoordinateFrame);
