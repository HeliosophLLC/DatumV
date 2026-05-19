using System.Collections.Concurrent;

namespace Heliosoph.DatumV.Models.Calibration;

/// <summary>
/// Process-wide map of model name → <see cref="ModelCalibration"/>. Lives
/// on <c>ModelCatalog</c> so it shares the catalog's lifetime; populated
/// from <see cref="CalibrationStore"/> at startup and saved back on
/// shutdown (or on a debounced background timer).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key choice.</strong> Keyed by model name (the same key the
/// catalog uses) rather than by file hash. Two entries pointing at the
/// same file is a misconfiguration we don't try to share calibration
/// across; the file-hash check inside each <see cref="ModelCalibration"/>
/// handles the "file changed underneath us" case.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> The dictionary itself uses
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> for lock-free
/// lookups; each <see cref="ModelCalibration"/> handles its own internal
/// locking for record / validate / clear operations.
/// </para>
/// </remarks>
public sealed class CalibrationRegistry
{
    private readonly ConcurrentDictionary<string, ModelCalibration> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the calibration record for <paramref name="modelName"/>,
    /// or <see langword="null"/> when no record exists. Use
    /// <see cref="GetOrCreate"/> when the caller intends to populate the
    /// record (e.g. the calibration coordinator).
    /// </summary>
    public ModelCalibration? Get(string modelName)
        => _entries.TryGetValue(modelName, out ModelCalibration? entry) ? entry : null;

    /// <summary>
    /// Returns the existing calibration record for
    /// <paramref name="modelName"/>, or creates a fresh
    /// <see cref="ModelCalibration.State.Uncalibrated"/> one bound to
    /// <paramref name="fileSha256"/>. If a record already exists with a
    /// different file hash, the old record is replaced — the on-disk
    /// file has changed since the last calibration, so the curve is
    /// stale.
    /// </summary>
    public ModelCalibration GetOrCreate(string modelName, string fileSha256)
    {
        return _entries.AddOrUpdate(
            modelName,
            addValueFactory: _ => new ModelCalibration(fileSha256),
            updateValueFactory: (_, existing) =>
                string.Equals(existing.FileSha256, fileSha256, StringComparison.OrdinalIgnoreCase)
                    ? existing
                    : new ModelCalibration(fileSha256));
    }

    /// <summary>
    /// Replaces (or inserts) the record for <paramref name="modelName"/>.
    /// Used by <see cref="CalibrationStore"/> when rehydrating from
    /// disk; callers writing new calibration data should go through
    /// <see cref="GetOrCreate"/> + the methods on
    /// <see cref="ModelCalibration"/> instead.
    /// </summary>
    public void Replace(string modelName, ModelCalibration calibration)
        => _entries[modelName] = calibration;

    /// <summary>
    /// Removes the record for <paramref name="modelName"/>. Returns true
    /// when an entry was removed; false when there was nothing to remove.
    /// Backs the SQL <c>RESET CALIBRATION</c> surface and the host-
    /// fingerprint-mismatch flush at startup.
    /// </summary>
    public bool Remove(string modelName) => _entries.TryRemove(modelName, out _);

    /// <summary>Removes every record. Used on fingerprint mismatch.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// All current entries, for <c>system.models</c> /
    /// <c>system.model_calibration</c> projection and for the persistence
    /// writer.
    /// </summary>
    public IReadOnlyDictionary<string, ModelCalibration> Snapshot()
    {
        return new Dictionary<string, ModelCalibration>(_entries, StringComparer.OrdinalIgnoreCase);
    }
}
