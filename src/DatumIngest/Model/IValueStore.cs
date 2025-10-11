namespace DatumIngest.Model;

/// <summary>
/// Unified contract for storing and retrieving reference-type payloads from a
/// backing store. Implemented by <see cref="ReferenceStore"/> (per-query object
/// registry) and <see cref="Arena"/> (contiguous byte buffer).
/// </summary>
/// <remarks>
/// Callers pass an <see cref="IValueStore"/> to <see cref="DataValue"/> factory
/// methods (write path) and to <c>As*</c> accessor methods (read path).
/// </remarks>
public interface IValueStore
{
    // ───────────────────────── Strings ─────────────────────────

    /// <summary>
    /// Stores a string value and returns two payload words to embed in a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="value">The string to store.</param>
    /// <returns>
    /// A pair of ints: for <see cref="ReferenceStore"/>, (index, 0);
    /// for <see cref="Arena"/>, (offset, length).
    /// </returns>
    (int P0, int P1) StoreString(string value);

    /// <summary>
    /// Retrieves a previously stored string using the payload words from a <see cref="DataValue"/>.
    /// </summary>
    string RetrieveString(int p0, int p1);

    /// <summary>
    /// Retrieves the raw UTF-8 bytes of a previously stored string without allocating
    /// a managed <see cref="string"/>. For <see cref="Arena"/>-backed stores this is a
    /// zero-copy slice of the backing buffer.
    /// </summary>
    ReadOnlySpan<byte> RetrieveUtf8Span(int p0, int p1);

    /// <summary>
    /// Stores raw UTF-8 bytes as a string value and returns two payload words.
    /// Avoids the intermediate <see cref="string"/> allocation when the source is
    /// already encoded (e.g. from <see cref="RetrieveUtf8Span"/>).
    /// </summary>
    (int P0, int P1) StoreUtf8(ReadOnlySpan<byte> utf8);

    /// <summary>
    /// Stores a <see cref="ReadOnlySpan{T}"/> of <see cref="char"/> by encoding it as
    /// UTF-8 and returns two payload words. Avoids a managed <see cref="string"/>
    /// allocation when the source is a decoded char span.
    /// </summary>
    (int P0, int P1) StoreChars(ReadOnlySpan<char> chars);

    // ───────────────────────── Byte arrays ─────────────────────────

    /// <summary>
    /// Stores a byte array and returns two payload words.
    /// </summary>
    (int P0, int P1) StoreBytes(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Retrieves a previously stored byte array.
    /// </summary>
    byte[] RetrieveBytes(int p0, int p1);

    // ───────────────────────── Float arrays ─────────────────────────

    /// <summary>
    /// Stores a float array and returns two payload words.
    /// </summary>
    (int P0, int P1) StoreFloats(ReadOnlySpan<float> floats);

    /// <summary>
    /// Retrieves a previously stored float array.
    /// </summary>
    float[] RetrieveFloats(int p0, int p1);

    // ───────────────────────── Tensors ─────────────────────────

    /// <summary>
    /// Stores a tensor (float data + int shape) and returns two payload words.
    /// </summary>
    (int P0, int P1) StoreTensor(ReadOnlySpan<float> data, ReadOnlySpan<int> shape);

    /// <summary>
    /// Retrieves a previously stored tensor, returning the float data and shape.
    /// </summary>
    float[] RetrieveTensor(int p0, int p1, out int[] shape);

    // ───────────────────────── DataValue arrays ─────────────────────────

    /// <summary>
    /// Stores a <see cref="DataValue"/> array and returns two payload words.
    /// </summary>
    (int P0, int P1) StoreDataValues(ReadOnlySpan<DataValue> values);

    /// <summary>
    /// Retrieves a previously stored <see cref="DataValue"/> array.
    /// </summary>
    DataValue[] RetrieveDataValues(int p0, int p1);

    // ───────────────────────── Arbitrary objects ─────────────────────────

    /// <summary>
    /// Stores an arbitrary managed object (e.g. <c>ImageHandle</c>).
    /// Only supported by <see cref="ReferenceStore"/>; <see cref="Arena"/> throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    (int P0, int P1) StoreObject(object value);

    /// <summary>
    /// Retrieves a previously stored object.
    /// </summary>
    object RetrieveObject(int p0, int p1);
}
