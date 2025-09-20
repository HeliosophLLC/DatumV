namespace DatumIngest.Model;

/// <summary>
/// Discriminator for the type of value stored in a <see cref="DataValue"/>.
/// Values 0–15 are the original type set. Values 16+ are extended numeric types
/// added for precise integer and double-precision support.
/// </summary>
public enum DataKind : byte
{
    /// <summary>A single unsigned 8-bit integer.</summary>
    UInt8 = 0,

    /// <summary>A single 32-bit floating-point number.</summary>
    Float32 = 1,

    /// <summary>A one-dimensional array of 32-bit floats (rank-1 tensor).</summary>
    Vector = 2,

    /// <summary>A two-dimensional array of 32-bit floats (rank-2 tensor).</summary>
    Matrix = 3,

    /// <summary>An N-dimensional array of 32-bit floats with arbitrary rank.</summary>
    Tensor = 4,

    /// <summary>A byte array containing raw binary data.</summary>
    UInt8Array = 5,

    /// <summary>A byte array containing encoded image data.</summary>
    Image = 6,

    /// <summary>A Unicode text string.</summary>
    String = 7,

    /// <summary>A calendar date without a time component.</summary>
    Date = 8,

    /// <summary>A date and time value.</summary>
    DateTime = 9,

    /// <summary>A raw JSON string for deferred parsing.</summary>
    JsonValue = 10,

    /// <summary>A 128-bit universally unique identifier (RFC 9562).</summary>
    Uuid = 11,

    /// <summary>A boolean value (true or false).</summary>
    Boolean = 12,

    /// <summary>A time-of-day without a date component.</summary>
    Time = 13,

    /// <summary>A duration (elapsed time span).</summary>
    Duration = 14,

    /// <summary>
    /// A typed array of <see cref="DataValue"/> elements sharing a common element kind.
    /// The element kind is stored in the shape metadata of the owning <see cref="DataValue"/>.
    /// </summary>
    Array = 15,

    // ───────────────────── Extended numeric types (16+) ─────────────────────

    /// <summary>A signed 8-bit integer (-128 to 127).</summary>
    Int8 = 16,

    /// <summary>A signed 16-bit integer (-32,768 to 32,767).</summary>
    Int16 = 17,

    /// <summary>An unsigned 16-bit integer (0 to 65,535).</summary>
    UInt16 = 18,

    /// <summary>A signed 32-bit integer (-2,147,483,648 to 2,147,483,647).</summary>
    Int32 = 19,

    /// <summary>An unsigned 32-bit integer (0 to 4,294,967,295).</summary>
    UInt32 = 20,

    /// <summary>A signed 64-bit integer.</summary>
    Int64 = 21,

    /// <summary>An unsigned 64-bit integer.</summary>
    UInt64 = 22,

    /// <summary>A 64-bit double-precision floating-point number.</summary>
    Float64 = 23,

    /// <summary>
    /// A named-field composite value. Field names and kinds are stored once in the
    /// enclosing <see cref="ColumnInfo.Fields"/> descriptor; each value holds a
    /// positional <see cref="DataValue"/>[] in the <see cref="ReferenceStore"/>.
    /// </summary>
    Struct = 24,
}
