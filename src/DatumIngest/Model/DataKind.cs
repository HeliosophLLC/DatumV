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

    /// <summary>A raw JSON string for deferred parsing.</summary>
    JsonValue = 49,

    /// <summary>A 128-bit universally unique identifier (RFC 9562).</summary>
    Uuid = 50,

    // ───────────────────────── Binary / blob (56–63) ─────────────────────────

    // 56 = retired UInt8Array enum value. Byte arrays now use Kind=UInt8 + IsArray
    // flag at both the DataValue and schema layers. The byte 56 is preserved as a
    // wire-format constant in DataValueWriter.WireKindByteArray; do not reuse here.

    /// <summary>A byte array containing encoded image data.</summary>
    Image = 57,

    // ───────────────────────── Collections &amp; composite (64–71) ─────────────────────────

    /// <summary>A one-dimensional array of 32-bit floats (rank-1 tensor).</summary>
    Vector = 64,

    /// <summary>
    /// A typed array of <see cref="DataValue"/> elements sharing a common element kind.
    /// The element kind is stored in the shape metadata of the owning <see cref="DataValue"/>.
    /// </summary>
    Array = 67,

    /// <summary>
    /// A named-field composite value. Field names and kinds are stored once in the
    /// enclosing <see cref="ColumnInfo.Fields"/> descriptor; each value holds a
    /// positional <see cref="DataValue"/>[] in the value store.
    /// </summary>
    Struct = 68,
}
