using System.Globalization;
using System.IO.Hashing;

namespace DatumIngest.Models.Calibration;

/// <summary>
/// Computes a content-based fingerprint of a model file on disk. Used as
/// the per-model invalidation key in <see cref="ModelCalibration"/>:
/// when the file's bytes change, the calibration curve no longer
/// describes the model that's actually loaded.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="XxHash64"/> rather than SHA-256: same "did the file
/// change" guarantee for our purposes (we're detecting weight changes,
/// not adversarial collisions), at roughly an order of magnitude higher
/// throughput. A 17 GB ONNX file hashes in ~2 seconds; SHA-256 of the
/// same file takes 30+ seconds and would be felt as a startup stall.
/// </para>
/// <para>
/// The output is the lowercase hexadecimal digest with a <c>"xxh64:"</c>
/// prefix. The prefix is durable in persisted JSON so a future migration
/// to a different hash function can co-exist (entries with the wrong
/// prefix are treated as mismatches and recalibrated).
/// </para>
/// </remarks>
public static class ModelFileFingerprint
{
    /// <summary>
    /// Prefix on fingerprint strings. Lets us evolve the hash function
    /// without false-positive matches against legacy data.
    /// </summary>
    public const string Prefix = "xxh64:";

    /// <summary>
    /// Computes the fingerprint of <paramref name="absolutePath"/>.
    /// Returns <see langword="null"/> when the file is missing or
    /// unreadable — callers should treat null as "cannot persist
    /// calibration for this model" and skip recording.
    /// </summary>
    public static string? TryCompute(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return null;

            XxHash64 hasher = new();
            using FileStream stream = new(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1 << 20,
                FileOptions.SequentialScan);

            byte[] buffer = new byte[1 << 20]; // 1 MiB chunks
            while (true)
            {
                int read = stream.Read(buffer);
                if (read == 0) break;
                hasher.Append(buffer.AsSpan(0, read));
            }

            return Prefix + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
