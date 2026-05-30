using System;
using System.Collections.Generic;
using System.Linq;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Default <see cref="IExportFormatRegistry"/> implementation. Resolves by
/// canonical name or by file extension. Name and extension collisions throw
/// at construction so misconfiguration fails fast.
/// </summary>
/// <remarks>
/// Export formats are an engine-level capability, not catalog state — they
/// aren't created or modified by SQL. The process-wide
/// <see cref="Default"/> singleton carries the formats the engine ships
/// with so the COPY planner can resolve them without threading a registry
/// through every catalog. DI hosts that want to enumerate or add formats
/// can register their own <see cref="IExportFormatRegistry"/>, but the
/// planner uses <see cref="Default"/> directly.
/// </remarks>
public sealed class ExportFormatRegistry : IExportFormatRegistry
{
    private static readonly Lazy<ExportFormatRegistry> _default = new(
        () => new ExportFormatRegistry([new Parquet.ParquetExportFormat()]));

    /// <summary>
    /// Process-wide default registry, populated with every format the engine
    /// ships built-in. Read by <see cref="Catalog.Plans.ExportPlan.PlanAsync"/>
    /// when no explicit registry is supplied.
    /// </summary>
    public static IExportFormatRegistry Default => _default.Value;

    private readonly Dictionary<string, IExportFormat> _byName;
    private readonly Dictionary<string, IExportFormat> _byExtension;

    /// <summary>Creates a registry over <paramref name="formats"/>.</summary>
    public ExportFormatRegistry(IEnumerable<IExportFormat> formats)
    {
        _byName = new(StringComparer.OrdinalIgnoreCase);
        _byExtension = new(StringComparer.OrdinalIgnoreCase);
        All = formats.ToArray();

        foreach (IExportFormat format in All)
        {
            if (!_byName.TryAdd(format.Name, format))
            {
                throw new InvalidOperationException(
                    $"Duplicate export format registration for name '{format.Name}'.");
            }
            foreach (string extension in format.Extensions)
            {
                if (!_byExtension.TryAdd(extension, format))
                {
                    throw new InvalidOperationException(
                        $"Duplicate export format registration for extension '{extension}' " +
                        $"(format '{format.Name}' conflicts with '{_byExtension[extension].Name}').");
                }
            }
        }
    }

    /// <inheritdoc />
    public IExportFormat? ResolveByName(string name)
        => _byName.TryGetValue(name, out IExportFormat? format) ? format : null;

    /// <inheritdoc />
    public IExportFormat? ResolveByExtension(string extension)
        => _byExtension.TryGetValue(extension, out IExportFormat? format) ? format : null;

    /// <inheritdoc />
    public IReadOnlyList<IExportFormat> All { get; }
}
