namespace Heliosoph.DatumV.Export;

/// <summary>
/// Where the sink writes bytes. Closed hierarchy — file path today, directory
/// and client-stream targets reserved for follow-ups (multi-file partitioned
/// sinks, COPY ... TO STDOUT for in-process UI streaming).
/// </summary>
public abstract record ExportTarget
{
    /// <summary>A single file on disk.</summary>
    public sealed record File(string Path) : ExportTarget;

    /// <summary>A directory on disk — for sinks that emit multiple files or sidecars.</summary>
    public sealed record Directory(string Path) : ExportTarget;
}
