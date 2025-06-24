namespace Axon.QueryEngine.Model;

/// <summary>
/// Discriminator for the type of value stored in a <see cref="DataValue"/>.
/// Members are ordered from narrowest to widest within the numeric widening chain.
/// </summary>
public enum DataKind
{
    /// <summary>A single unsigned 8-bit integer.</summary>
    UInt8 = 0,

    /// <summary>A single 32-bit floating-point number.</summary>
    Scalar = 1,

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
}
