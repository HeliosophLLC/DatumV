namespace Heliosoph.DatumV.DatumFile.Sidecar;

/// <summary>
/// Append-only sink for Large Binary Objects (images, byte arrays, future video, etc.)
/// destined for a long-lived destination such as a <c>.datum-blob</c> sidecar file.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="Model.IValueStore"/>, which uses 32-bit <c>(int, int)</c>
/// coordinates and is sized for per-batch arena lifetimes. <see cref="IBlobSink"/> uses
/// 64-bit offset and length so a single sidecar can address terabytes of binary payload
/// without a file-size cap.
/// </para>
/// <para>
/// Implementations must be safe for concurrent <see cref="Append"/> calls; serialisation
/// happens internally so callers see the offset assigned to their bytes without racing.
/// </para>
/// </remarks>
public interface IBlobSink
{
    /// <summary>
    /// Appends the given bytes to the sink and returns their final position. The
    /// returned <c>Offset</c> and <c>Length</c> are absolute coordinates that
    /// downstream <see cref="Model.DataValue"/>s embed verbatim — no later relocation
    /// or rewriting is required.
    /// </summary>
    /// <param name="bytes">The bytes to append.</param>
    /// <returns>
    /// <c>(Offset, Length)</c> where <c>Offset</c> is the absolute byte position in
    /// the sink at which <paramref name="bytes"/> were written, and <c>Length</c>
    /// equals <paramref name="bytes"/>.Length.
    /// </returns>
    (long Offset, long Length) Append(ReadOnlySpan<byte> bytes);
}
