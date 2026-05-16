using DatumIngest.Data;
using DatumIngest.Model;

namespace DatumIngest.Tests.Data;

/// <summary>
/// Sync wrappers over the async <see cref="InProcessDatumDbCommand"/> /
/// <see cref="InProcessDatumDbReader"/> surface. Test-assembly only — the
/// production types are async-first and intentionally expose no
/// sync-over-async methods so production code cannot block a worker
/// thread by accident.
/// </summary>
/// <remarks>
/// Per the C1g async cleanup rule: no new
/// <c>.GetAwaiter().GetResult()</c> in <c>src/</c>; sync-over-async
/// bridges live here. Tests that exercise the readable-API shape used
/// elsewhere in the codebase (ADO-style <c>using</c> + sync iteration)
/// reach for these extensions.
/// </remarks>
internal static class InProcessDatumDbSyncExtensions
{
    /// <summary>Sync wrapper over <see cref="InProcessDatumDbReader.ReadAsync"/>.</summary>
    public static bool Read(this InProcessDatumDbReader reader)
        => reader.ReadAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Sync wrapper over <see cref="InProcessDatumDbCommand.ExecuteReaderAsync"/>.</summary>
    public static InProcessDatumDbReader ExecuteReader(this InProcessDatumDbCommand command)
        => command.ExecuteReaderAsync().GetAwaiter().GetResult();

    /// <summary>Sync wrapper over <see cref="InProcessDatumDbCommand.ExecuteNonQueryAsync"/>.</summary>
    public static int ExecuteNonQuery(this InProcessDatumDbCommand command)
        => command.ExecuteNonQueryAsync().GetAwaiter().GetResult();

    /// <summary>Sync wrapper over <see cref="InProcessDatumDbCommand.ExecuteScalarAsync"/>.</summary>
    public static DataValue? ExecuteScalar(this InProcessDatumDbCommand command)
        => command.ExecuteScalarAsync().GetAwaiter().GetResult();
}
