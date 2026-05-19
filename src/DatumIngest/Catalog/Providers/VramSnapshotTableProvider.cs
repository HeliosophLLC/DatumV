using System.Runtime.CompilerServices;

using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Single-row virtual table exposing the live NVML VRAM reading plus the
/// residency manager's internal accounting. Companion to
/// <see cref="ResidencySnapshotTableProvider"/> which lists per-model
/// rows; this table is the device-wide top-level summary.
/// </summary>
/// <remarks>
/// On hosts without NVML (CPU-only, AMD/Intel GPU, init failure) the
/// device columns are NULL. <c>committed_bytes</c> always reflects what
/// the residency manager believes it owns.
/// </remarks>
public sealed class VramSnapshotTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.vram_snapshot";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "vram_snapshot");

    private static readonly Schema _schema = BuildSchema();

    private readonly ModelCatalog _modelCatalog;

    /// <summary>Creates a provider surfacing the live NVML reading + the catalog's residency-manager accounting.</summary>
    public VramSnapshotTableProvider(Pool pool, ModelCatalog modelCatalog)
        : base(pool, QualifiedTableName)
    {
        _modelCatalog = modelCatalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 1;

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
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch batch = Pool.RentRowBatch(lookup, capacity: 1, targetArena);
        DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);

        bool nvmlAvailable = VramProbe.TryGetUsage(out long deviceUsed, out long deviceTotal);

        cells[0] = nvmlAvailable
            ? DataValue.FromInt64(deviceTotal)
            : DataValue.Null(DataKind.Int64);
        cells[1] = nvmlAvailable
            ? DataValue.FromInt64(deviceUsed)
            : DataValue.Null(DataKind.Int64);
        cells[2] = nvmlAvailable
            ? DataValue.FromInt64(Math.Max(0, deviceTotal - deviceUsed))
            : DataValue.Null(DataKind.Int64);
        cells[3] = DataValue.FromInt64(_modelCatalog.ResidencyManager.VramUsedBytes);
        cells[4] = _modelCatalog.ResidencyManager.VramBudgetBytes < 0
            ? DataValue.Null(DataKind.Int64)
            : DataValue.FromInt64(_modelCatalog.ResidencyManager.VramBudgetBytes);

        batch.Add(cells);
        yield return batch;
        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("device_total_bytes",  DataKind.Int64, nullable: true),
        new ColumnInfo("device_used_bytes",   DataKind.Int64, nullable: true),
        new ColumnInfo("device_free_bytes",   DataKind.Int64, nullable: true),
        new ColumnInfo("committed_bytes",     DataKind.Int64, nullable: false),
        new ColumnInfo("budget_bytes",        DataKind.Int64, nullable: true),
    ]);
}
