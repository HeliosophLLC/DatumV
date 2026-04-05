using System.Runtime.CompilerServices;

using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table exposing <see cref="ModelResidencyManager.Snapshot"/> as
/// live-queryable SQL rows. One row per model currently resident in VRAM
/// with its weight cost, active lease count, and last-used timestamp.
/// Companion to <see cref="VramSnapshotTableProvider"/> which surfaces
/// the device-wide NVML reading.
/// </summary>
/// <remarks>
/// <para>
/// Use cases:
/// <list type="bullet">
///   <item>UI status bar (<c>SELECT name, weight_cost_bytes, active_refs FROM system_residency_snapshot</c>)</item>
///   <item>Eviction triage (<c>SELECT name FROM system_residency_snapshot WHERE active_refs = 0 ORDER BY last_used_at</c>)</item>
///   <item>Memory accounting (<c>SELECT SUM(weight_cost_bytes) FROM system_residency_snapshot</c>)</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ResidencySnapshotTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.residency_snapshot";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "residency_snapshot");

    private static readonly Schema _schema = BuildSchema();

    private readonly ModelCatalog _modelCatalog;

    /// <summary>
    /// Creates a provider surfacing <paramref name="modelCatalog"/>'s
    /// residency manager snapshot.
    /// </summary>
    public ResidencySnapshotTableProvider(Pool pool, ModelCatalog modelCatalog)
        : base(pool, QualifiedTableName)
    {
        _modelCatalog = modelCatalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
        => _modelCatalog.ResidencyManager.Snapshot().Count;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Model.TypeIdTranslationTable? typeIdTranslations = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        IReadOnlyList<(string Name, long Bytes, int ActiveRefs)> snapshot =
            _modelCatalog.ResidencyManager.Snapshot();

        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        // Sort by name for stable iteration; UIs that diff snapshot to
        // snapshot benefit from consistent ordering.
        foreach ((string name, long bytes, int activeRefs) in
            snapshot.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
            cells[0] = DataValue.FromString(name, batch.Arena);
            cells[1] = DataValue.FromInt64(bytes);
            cells[2] = DataValue.FromInt32(activeRefs);
            batch.Add(cells);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null) yield return batch;
        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",              DataKind.String, nullable: false),
        new ColumnInfo("weight_cost_bytes", DataKind.Int64,  nullable: false),
        new ColumnInfo("active_refs",       DataKind.Int32,  nullable: false),
    ]);
}
