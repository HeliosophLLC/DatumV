using System.Collections.Generic;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// DI-resolvable lookup for <see cref="IExportFormat"/> implementations.
/// COPY planning hits this twice: once for an explicit FORMAT option
/// (<see cref="ResolveByName"/>), once as a fallback when FORMAT is absent
/// and the target path's extension uniquely identifies a format
/// (<see cref="ResolveByExtension"/>).
/// </summary>
public interface IExportFormatRegistry
{
    /// <summary>
    /// Looks up a format by case-insensitive canonical name. Returns
    /// <see langword="null"/> when the name is unknown — the planner turns
    /// that into a user-facing <see cref="ExportPlanException"/>.
    /// </summary>
    IExportFormat? ResolveByName(string name);

    /// <summary>
    /// Looks up a format by file extension (including leading <c>.</c>),
    /// case-insensitive. Returns <see langword="null"/> when no registered
    /// format claims the extension.
    /// </summary>
    IExportFormat? ResolveByExtension(string extension);

    /// <summary>All registered formats, in registration order.</summary>
    IReadOnlyList<IExportFormat> All { get; }
}
