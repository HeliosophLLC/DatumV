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
    JsonPointCloudInfo? PointCloud = null,
    // Populated for kind="mesh". Parallel to PointCloud — lets the grid
    // render a metadata-only chip without decoding the blob, and gives
    // the 3D viewer the shape it needs to set up BufferAttributes /
    // indices without re-parsing the header.
    JsonMeshInfo? Mesh = null,
    // Populated for kind="numeric_array". DataB64 carries the raw
    // little-endian element bytes (no gzip — the binary form already beats
    // the JSON-text form ~3× on wire size, and the decode path is a single
    // typed-array view). Min/Max/Mean are computed server-side so the
    // front-end can render a stats card without touching the bytes.
    string? ElementKind = null,
    int? Count = null,
    // Logical shape for multi-dimensional arrays — e.g. [4, 4] for a 4×4
    // matrix, [3, 480, 640] for a CHW tensor. Null/omitted for flat 1-D
    // arrays. Element count equals Product(Shape) when set. The DataB64
    // bytes are flat row-major (last dimension varies fastest).
    IReadOnlyList<int>? Shape = null,
    double? Min = null,
    double? Max = null,
    double? Mean = null,
    // Populated for kind="struct". Each entry recursively carries a
    // full JsonCell so nested numeric arrays / images / sub-structs
    // get their own dedicated renderer on the client instead of being
    // flattened into a single JSON text body that inlines megabytes
    // of base64 / `[0.1, 0.2, ...]`.
    IReadOnlyList<JsonStructField>? Fields = null,
    // Populated for kind="json" when the value's JSON form was truncated
    // to a bounded preview. `Text` holds the partial-but-still-valid JSON;
    // this carries the total/shown counts so the front-end can render a
    // "N of M shown" affordance without re-parsing.
    JsonPreviewInfo? JsonPreview = null);

internal sealed record JsonStructField(string Name, JsonCell Cell);

internal sealed record JsonMediaItem(string Mime, string DataB64);

internal sealed record JsonPointCloudInfo(
    int PointCount,
    bool HasColor,
    int Width,
    int Height,
    string CoordinateFrame);

internal sealed record JsonMeshInfo(
    int VertexCount,
    int TriangleCount,
    bool HasColor,
    bool HasNormals,
    bool HasUVs,
    bool HasTexture,
    string CoordinateFrame,
    // Bbox corners for client-side camera framing — saves re-decoding the
    // 48-byte header before the viewer can compute its initial camera.
    float[] BboxMin,
    float[] BboxMax);
