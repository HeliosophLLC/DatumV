namespace DatumIngest.Model;

/// <summary>
/// Discriminator for the type of value stored in a <see cref="DataValue"/>.
/// Values are organized into 8-aligned category blocks, each with room for
/// future additions within the category. The enum is <c>byte</c>-backed (0–255).
/// </summary>
public enum DataKind : byte
{
    // ───────────────────────── Meta / Sentinel (0–7) ─────────────────────────

    /// <summary>
    /// Sentinel for an unknown or untyped value. <c>default(DataValue)</c> has this kind.
    /// When the <see cref="DataValue.IsNull"/> flag is set, represents an untyped SQL NULL
    /// (e.g. bare <c>SELECT NULL</c>); otherwise indicates the value has not been assigned
    /// a concrete type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A type tag value that describes another <see cref="DataKind"/>.
    /// Produced by the <c>typeof()</c> function and compared against type literals
    /// (e.g. <c>typeof(x) == Int32</c>).
    /// </summary>
    Type = 1,

    // ───────────────────────── Boolean (8–15) ─────────────────────────

    /// <summary>A boolean value (true or false).</summary>
    Boolean = 8,

    // ───────────────────────── Unsigned integers (16–23) ─────────────────────────

    /// <summary>A single unsigned 8-bit integer.</summary>
    UInt8 = 16,

    /// <summary>An unsigned 16-bit integer (0 to 65,535).</summary>
    UInt16 = 17,

    /// <summary>An unsigned 32-bit integer (0 to 4,294,967,295).</summary>
    UInt32 = 18,

    /// <summary>An unsigned 64-bit integer.</summary>
    UInt64 = 19,

    /// <summary>An unsigned 128-bit integer (.NET <see cref="System.UInt128"/>).</summary>
    UInt128 = 20,

    // ───────────────────────── Signed integers (24–31) ─────────────────────────

    /// <summary>A signed 8-bit integer (-128 to 127).</summary>
    Int8 = 24,

    /// <summary>A signed 16-bit integer (-32,768 to 32,767).</summary>
    Int16 = 25,

    /// <summary>A signed 32-bit integer (-2,147,483,648 to 2,147,483,647).</summary>
    Int32 = 26,

    /// <summary>A signed 64-bit integer.</summary>
    Int64 = 27,

    /// <summary>A signed 128-bit integer (.NET <see cref="System.Int128"/>).</summary>
    Int128 = 28,

    // ───────────────────────── Floating point (32–39) ─────────────────────────

    // 32 = Float8 (future, e.g. E4M3/E5M2)

    /// <summary>A 16-bit IEEE 754 binary16 floating-point number (.NET <see cref="Half"/>).</summary>
    Float16 = 33,

    /// <summary>A single 32-bit floating-point number.</summary>
    Float32 = 34,

    /// <summary>A 64-bit double-precision floating-point number.</summary>
    Float64 = 35,

    // 36 = Float128 (future)

    /// <summary>A 128-bit decimal floating-point number (.NET <see cref="decimal"/>).</summary>
    Decimal = 37,

    // ───────────────────────── Temporal (40–47) ─────────────────────────

    /// <summary>A calendar date without a time component.</summary>
    Date = 40,

    /// <summary>A time-of-day without a date component.</summary>
    Time = 41,

    /// <summary>A date and time value.</summary>
    DateTime = 42,

    /// <summary>A duration (elapsed time span).</summary>
    Duration = 43,

    // ───────────────────────── Text &amp; identifiers (48–55) ─────────────────────────

    /// <summary>A Unicode text string.</summary>
    String = 48,

    /// <summary>A 128-bit universally unique identifier (RFC 9562).</summary>
    Uuid = 50,

    // ───────────────────────── Binary / blob (56–63) ─────────────────────────

    // 56 = retired UInt8Array enum value. Byte arrays now use Kind=UInt8 + IsArray
    // flag at both the DataValue and schema layers. The byte 56 is preserved as a
    // wire-format constant in DataValueWriter.WireKindByteArray; do not reuse here.

    /// <summary>A byte array containing encoded image data.</summary>
    Image = 57,

    /// <summary>A byte array containing encoded audio data (e.g. WAV, MP3, FLAC, OGG).</summary>
    Audio = 58,

    /// <summary>
    /// Reserved slot for a runtime-only lazy handle to a windowed slice of
    /// <see cref="Audio"/>. Inline payload (when implemented) is
    /// <c>(audio_id, start_sample, end_sample_exclusive)</c>; PCM materialisation
    /// is deferred to the consuming accessor. The first audio inference workloads
    /// (Whisper, Silero VAD, pyannote) all consume fixed-rate PCM windows, so
    /// "slice" rather than "frame" captures the intended granularity. Pairs with
    /// <see cref="VideoSlice"/> in the "media + frame-handle + slice-handle"
    /// convention.
    /// </summary>
    AudioSlice = 59,

    /// <summary>A byte array containing encoded video data (e.g. MP4, WebM, MKV).</summary>
    Video = 60,

    /// <summary>
    /// A runtime-only lazy handle to a single frame of <see cref="Video"/>. The
    /// inline payload at <c>(_p0, _p1)</c> is <c>(video_id, frame_index)</c>;
    /// pixel materialisation is deferred to the consuming accessor, which routes
    /// through a per-query video registry. Writing a VideoFrame to a persisted
    /// column materialises it to <see cref="Image"/> first.
    /// </summary>
    VideoFrame = 61,

    /// <summary>
    /// Reserved slot for a runtime-only lazy handle to a frame-range of
    /// <see cref="Video"/>. Inline payload (when implemented) is
    /// <c>(video_id, start_frame, end_frame_exclusive)</c>. Acts as the input to
    /// <c>video_unnest_frames</c> for bounded iteration and to temporal-model
    /// invocations (action recognition, video classification) that operate on
    /// frame windows rather than single frames. Pairs with <see cref="AudioSlice"/>.
    /// </summary>
    VideoSlice = 62,

    // 63 = free

    // ───────────────────────── Collections &amp; composite (64–71) ─────────────────────────

    /// <summary>
    /// A JSON document, stored as canonical CBOR bytes (RFC 7049 §3.9). Wire
    /// representation is bytes-in-arena, addressed at <c>(_p0, _p1)</c>; payload
    /// is structured (object/array/scalar/null) and accessed via the <c>json_*</c>
    /// function family. Canonical encoding lets two semantically-equal JSON
    /// inputs hash and compare via raw byte equality.
    /// </summary>
    Json = 64,

    /// <summary>
    /// A named-field composite value. Field names and kinds are stored once in the
    /// enclosing <see cref="ColumnInfo.Fields"/> descriptor; each value holds a
    /// positional <see cref="DataValue"/>[] in the value store.
    /// </summary>
    Struct = 68,

    // ───────────────────────── Spatial / geometric (72–79) ─────────────────────────

    /// <summary>
    /// A 2D point with single-precision X and Y components (<see cref="System.Numerics.Vector2"/>).
    /// 8 bytes inline (two <see cref="float"/>s in <c>_p0</c>/<c>_p1</c>). Constructed via
    /// <c>point2d(x, y)</c>; component access via <c>p.x</c> / <c>p.y</c> (sugar for
    /// <c>point_x(p)</c> / <c>point_y(p)</c>).
    /// </summary>
    Point2D = 72,

    /// <summary>
    /// A 3D point with single-precision X, Y, Z components (<see cref="System.Numerics.Vector3"/>).
    /// 12 bytes inline (three <see cref="float"/>s in <c>_p0</c>/<c>_p1</c>/<c>_p2</c>).
    /// Constructed via <c>point3d(x, y, z)</c>; component access via <c>p.x</c> / <c>p.y</c> /
    /// <c>p.z</c> (sugar for <c>point_x</c> / <c>point_y</c> / <c>point_z</c>).
    /// </summary>
    Point3D = 73,

    /// <summary>
    /// A dense collection of 3D points with optional per-point attributes (color, normal, intensity).
    /// Storage is a single byte blob: a 40-byte header (see <c>PointCloudHeader</c>) followed by
    /// a fixed-stride interleaved per-point payload. Shares <see cref="DataKind.Image"/>'s arena /
    /// sidecar storage shape; accessed via <c>AsPointCloud</c> / <c>AsByteSpan</c>.
    /// </summary>
    PointCloud = 74,

    /// <summary>
    /// A 3D triangle mesh with optional per-vertex attributes (color, normal, UVs) and an optional
    /// embedded texture. Storage is a single byte blob: a 48-byte header (see <c>MeshHeader</c>)
    /// followed by an interleaved per-vertex payload, a triangle-index array, and optionally an
    /// encoded texture image at the tail. Shares <see cref="DataKind.Image"/>'s arena / sidecar
    /// storage shape; accessed via <c>AsMesh</c> / <c>AsByteSpan</c>.
    /// </summary>
    Mesh = 75,
}
