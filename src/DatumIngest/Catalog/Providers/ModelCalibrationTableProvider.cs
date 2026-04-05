using System.Runtime.CompilerServices;

using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Calibration;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table that exposes per-batch calibration curve entries from
/// <see cref="ModelCatalog.CalibrationRegistry"/>. One row per
/// (model_name, batch_size). The companion <c>system.models</c> table
/// surfaces a summary view (max batch, weight cost, state); this provider
/// is for inspecting the full curve.
/// </summary>
/// <remarks>
/// <para>
/// Typical use: <c>SELECT * FROM system_model_calibration WHERE model_name
/// = 'depth_anything_v2_small' ORDER BY batch_size;</c> shows what the
/// engine believes each batch size costs in VRAM, how many observations
/// back the belief, and when each entry was last validated against a
/// real dispatch.
/// </para>
/// <para>
/// Rows materialise on every <see cref="ScanAsync"/> call so live
/// recalibration is immediately visible — same liveness contract as
/// <c>system.models</c>.
/// </para>
/// <para>
/// Schema (6 columns):
/// <list type="table">
///   <item><term>model_name</term><description>SQL identifier matching the corresponding <c>system.models.name</c>.</description></item>
///   <item><term>batch_size</term><description>The dispatch batch size this entry describes.</description></item>
///   <item><term>total_vram_bytes</term><description>Peak VRAM observed during a fresh-load dispatch at this batch size (weights + activations), in bytes. Subtract <c>system.models.weight_cost_bytes</c> for activation cost.</description></item>
///   <item><term>observation_count</term><description>How many measurements contributed to this entry. With absolute-totals semantics, always 1 per ramp step.</description></item>
///   <item><term>last_validated_at</term><description>UTC timestamp of the most recent confirming observation.</description></item>
///   <item><term>calibration_state</term><description>Mirrored from <c>system.models</c> — <c>uncalibrated</c> / <c>calibrated</c> / <c>stale</c>. Joins/filters on calibration health stay local to this table.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModelCalibrationTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.model_calibration";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "model_calibration");

    private static readonly Schema _schema = BuildSchema();

    private readonly ModelCatalog _modelCatalog;

    /// <summary>
    /// Creates a provider that surfaces <paramref name="modelCatalog"/>'s
    /// calibration registry. The registry is held by reference — entries
    /// recorded after construction are visible to subsequent scans.
    /// </summary>
    public ModelCalibrationTableProvider(Pool pool, ModelCatalog modelCatalog)
        : base(pool, QualifiedTableName)
    {
        _modelCatalog = modelCatalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        long total = 0;
        foreach (ModelCalibration calibration in _modelCatalog.CalibrationRegistry.Snapshot().Values)
        {
            total += calibration.Curve.Count;
        }
        return total;
    }

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

        // Snapshot the registry so a recalibration completing mid-scan
        // doesn't produce duplicate / missing rows. Snapshot is a copy of
        // the dictionary references, not the underlying ModelCalibration
        // objects — entries still mutate during the scan, but the set of
        // models we'll visit is fixed. Per-entry curve reads use the
        // ModelCalibration's internal lock so the per-row data is
        // consistent.
        IReadOnlyDictionary<string, ModelCalibration> snapshot =
            _modelCatalog.CalibrationRegistry.Snapshot();

        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        // Stable ordering: model_name ascending, then batch_size ascending
        // (the SortedDictionary inside ModelCalibration enforces the
        // second part natively).
        foreach (string modelName in snapshot.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ModelCalibration calibration = snapshot[modelName];
            string stateName = CalibrationStateName(calibration.Status);
            IReadOnlyDictionary<int, CalibrationEntry> curve = calibration.Curve;
            if (curve.Count == 0) continue;

            foreach ((int batchSize, CalibrationEntry entry) in curve)
            {
                cancellationToken.ThrowIfCancellationRequested();

                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

                DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
                cells[0] = DataValue.FromString(modelName, batch.Arena);
                cells[1] = DataValue.FromInt32(batchSize);
                cells[2] = DataValue.FromInt64(entry.TotalVramBytes);
                cells[3] = DataValue.FromInt32(entry.ObservationCount);
                cells[4] = DataValue.FromTimestampTz(entry.LastValidatedAt);
                cells[5] = DataValue.FromString(stateName, batch.Arena);
                batch.Add(cells);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors <see cref="ModelsTableProvider"/>'s state-name mapping so
    /// the two surfaces present identical strings for the same enum.
    /// </summary>
    private static string CalibrationStateName(ModelCalibration.State state) => state switch
    {
        ModelCalibration.State.Uncalibrated => "uncalibrated",
        ModelCalibration.State.Calibrated => "calibrated",
        ModelCalibration.State.Stale => "stale",
        _ => "unknown",
    };

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("model_name",          DataKind.String,      nullable: false),
        new ColumnInfo("batch_size",          DataKind.Int32,       nullable: false),
        new ColumnInfo("total_vram_bytes",    DataKind.Int64,       nullable: false),
        new ColumnInfo("observation_count",   DataKind.Int32,       nullable: false),
        new ColumnInfo("last_validated_at",   DataKind.TimestampTz, nullable: false),
        new ColumnInfo("calibration_state",   DataKind.String,      nullable: false),
    ]);
}
