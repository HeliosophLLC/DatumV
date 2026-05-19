using System.Diagnostics;
using System.Security.Cryptography;

namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Source of the <see cref="FooterPrologueV4.WriterId"/> stamp written
/// on every commit. Each <see cref="DatumFileWriterV2"/> picks an id at
/// construction; for single-writer workloads a stable per-process value
/// is sufficient, while multi-writer scenarios will configure distinct
/// ids per writer instance to attribute commits.
/// </summary>
/// <remarks>
/// <para>
/// Format-wise the id is purely informational — readers don't compare
/// it against anything and won't change behavior based on it. It exists
/// for after-the-fact diagnostics ("which writer instance produced
/// generation 47?") and as a forward-compat hook so multi-writer
/// commits can later be attributed without another format bump.
/// </para>
/// <para>
/// The default value is derived once per process from
/// <see cref="Process.Id"/> mixed with 8 random bytes. This avoids two
/// different processes accidentally colliding on the same id when both
/// write to the same file (e.g. a CLI ingest plus a service running
/// under the same uid). It is not a security boundary — it's a
/// debugging aid.
/// </para>
/// </remarks>
public static class WriterIdentity
{
    private static readonly Lazy<ulong> _default = new(GenerateProcessStableId);

    /// <summary>
    /// The default writer id used when the caller doesn't configure one
    /// explicitly. Stable for the lifetime of the process; differs
    /// across process restarts.
    /// </summary>
    public static ulong Default => _default.Value;

    /// <summary>
    /// Writer id reserved to mean "no specific writer." Always written
    /// by readers that don't care to attribute, and the value present
    /// in the footer prologue when older code paths chose not to set
    /// one. Reserved — callers shouldn't pick this themselves.
    /// </summary>
    public const ulong Anonymous = 0UL;

    private static ulong GenerateProcessStableId()
    {
        // Mix process id with 8 random bytes so two processes with the
        // same id don't collide. xor avoids exposing the random bytes
        // in the high bits if the caller logs only the low 32.
        Span<byte> randomBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(randomBytes);
        ulong random = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(randomBytes);
        ulong pid = (uint)Environment.ProcessId;
        ulong id = random ^ (pid << 32) ^ pid;

        // Avoid collisions with the reserved Anonymous sentinel. Wrap
        // to 1 in the (vanishingly rare) case the xor lands at zero.
        return id == Anonymous ? 1UL : id;
    }
}
