using System.Text.Json;
using System.Text.Json.Serialization;

using DatumIngest.Diagnostics;

namespace DatumIngest.Models.Calibration;

/// <summary>
/// File-backed persistence for <see cref="CalibrationRegistry"/>. The
/// calibration cache lives in a per-host directory
/// (<c>%LOCALAPPDATA%/DatumIngest/calibration.json</c> on Windows;
/// <c>~/.cache/DatumIngest/calibration.json</c> elsewhere) so it follows
/// the machine, not the catalog. Two engine processes on the same host
/// share the file; last-writer-wins is acceptable for advisory data.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invalidation.</strong> On <see cref="Load"/>, the persisted
/// <see cref="HostFingerprint"/> is compared against the live host. Any
/// mismatch discards the entire file's worth of model curves — they
/// described a different GPU / driver / ORT version. Per-model
/// invalidation (file SHA-256 changed) happens at
/// <see cref="CalibrationRegistry.GetOrCreate"/> time once the catalog
/// is populated.
/// </para>
/// <para>
/// <strong>Failure modes.</strong> Missing file, corrupted JSON, IO
/// errors: all return an empty registry rather than throwing. The
/// engine recalibrates from scratch — a slow first query is better than
/// a startup failure over advisory cache state.
/// </para>
/// </remarks>
public sealed class CalibrationStore
{
    /// <summary>
    /// Default file location. <c>%LOCALAPPDATA%/DatumIngest/calibration.json</c>
    /// on Windows; <c>~/.local/share/DatumIngest/calibration.json</c> on
    /// Linux/macOS via <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DatumIngest",
        "calibration.json");

    private readonly string _filePath;

    /// <summary>
    /// Constructs a store rooted at <paramref name="filePath"/>.
    /// <see langword="null"/> uses <see cref="DefaultFilePath"/>.
    /// </summary>
    public CalibrationStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>Absolute path of the backing file.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Loads persisted calibration into <paramref name="registry"/>.
    /// Discards the file's contents (returns an empty registry contribution)
    /// when the recorded <see cref="HostFingerprint"/> doesn't match
    /// <paramref name="liveFingerprint"/>, when the file is missing, or
    /// when parsing fails. Never throws.
    /// </summary>
    /// <returns>
    /// The <see cref="LoadResult"/> describing what happened — useful for
    /// surfacing "rehydrated 12 models" or "fingerprint mismatch, started
    /// fresh" at startup.
    /// </returns>
    public LoadResult Load(CalibrationRegistry registry, HostFingerprint liveFingerprint)
    {
        if (!File.Exists(_filePath)) return LoadResult.NoFile;

        CalibrationFileDto? parsed;
        try
        {
            string json = File.ReadAllText(_filePath);
            parsed = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationFileDto);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return LoadResult.ParseError;
        }

        if (parsed?.Host is null || parsed.Models is null) return LoadResult.ParseError;

        HostFingerprint persistedFingerprint = new(
            parsed.Host.GpuUuid,
            parsed.Host.VramTotalBytes,
            parsed.Host.DriverVersion,
            parsed.Host.OrtVersion);

        if (!persistedFingerprint.Matches(liveFingerprint))
        {
            return LoadResult.FingerprintMismatch;
        }

        int loaded = 0;
        foreach ((string modelName, ModelCalibrationDto modelDto) in parsed.Models)
        {
            if (modelDto.FileSha256 is null) continue;

            Dictionary<int, CalibrationEntry> curve = [];
            if (modelDto.Curve is not null)
            {
                foreach ((string batchStr, CalibrationEntryDto entryDto) in modelDto.Curve)
                {
                    if (!int.TryParse(batchStr, out int batch) || batch <= 0) continue;
                    // Skip entries with no recorded total. Pre-totals
                    // calibration files (which had `marginal_vram_bytes`
                    // and no `total_vram_bytes`) deserialize with
                    // TotalVramBytes=0 here and get silently dropped —
                    // the file rolls forward by recalibrating those
                    // models on next use, rather than discarding the
                    // whole file or trying to interpret old marginals
                    // as totals.
                    if (entryDto.TotalVramBytes <= 0) continue;
                    DateTimeOffset lastValidated = DateTimeOffset.TryParse(
                        entryDto.LastValidatedAt, out DateTimeOffset parsedAt) ? parsedAt : DateTimeOffset.UtcNow;
                    curve[batch] = new CalibrationEntry(
                        entryDto.TotalVramBytes,
                        entryDto.ObservationCount,
                        lastValidated);
                }
            }

            registry.Replace(modelName, new ModelCalibration(
                modelDto.FileSha256,
                modelDto.WeightCostBytes,
                curve));
            loaded++;
        }

        return new LoadResult(LoadStatus.Loaded, loaded);
    }

    /// <summary>
    /// Writes the registry's current contents to disk, stamped with
    /// <paramref name="fingerprint"/>. Creates the parent directory when
    /// needed. Last-writer-wins on contention; safe to call from a
    /// debounced background timer.
    /// </summary>
    public void Save(CalibrationRegistry registry, HostFingerprint fingerprint)
    {
        IReadOnlyDictionary<string, ModelCalibration> snapshot = registry.Snapshot();

        CalibrationFileDto dto = new()
        {
            Host = new HostFingerprintDto
            {
                GpuUuid = fingerprint.GpuUuid,
                VramTotalBytes = fingerprint.VramTotalBytes,
                DriverVersion = fingerprint.DriverVersion,
                OrtVersion = fingerprint.OrtVersion,
            },
            Models = [],
        };

        foreach ((string modelName, ModelCalibration calibration) in snapshot)
        {
            // Skip truly-empty entries (just-created registry slots with
            // no recorded data). An Uncalibrated entry that has a
            // weight_cost from a load-time measurement OR has curve
            // entries from a partial ramp IS worth persisting — those
            // are the values that survive across restarts and prevent
            // the user from seeing NULL weight_cost_bytes after exiting
            // before the calibration coordinator had a chance to seal
            // the entry as Calibrated. Stale entries always persist so
            // users can inspect what was measured before the drift /
            // spill that demoted them.
            if (calibration.Status == ModelCalibration.State.Uncalibrated
                && calibration.WeightCostBytes <= 0
                && calibration.Curve.Count == 0) continue;

            Dictionary<string, CalibrationEntryDto> curveDto = [];
            foreach ((int batch, CalibrationEntry entry) in calibration.Curve)
            {
                curveDto[batch.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new CalibrationEntryDto
                {
                    TotalVramBytes = entry.TotalVramBytes,
                    ObservationCount = entry.ObservationCount,
                    LastValidatedAt = entry.LastValidatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            dto.Models[modelName] = new ModelCalibrationDto
            {
                FileSha256 = calibration.FileSha256,
                WeightCostBytes = calibration.WeightCostBytes,
                Curve = curveDto,
            };
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(dto, CalibrationJsonContext.Default.CalibrationFileDto);
        File.WriteAllText(_filePath, json);
    }
}

/// <summary>
/// Outcome of a <see cref="CalibrationStore.Load"/> call.
/// </summary>
public sealed record LoadResult(LoadStatus Status, int LoadedCount = 0)
{
    /// <summary>No file at the configured path — first run on this host.</summary>
    public static readonly LoadResult NoFile = new(LoadStatus.NoFile);

    /// <summary>File present but unparseable; treated as if it didn't exist.</summary>
    public static readonly LoadResult ParseError = new(LoadStatus.ParseError);

    /// <summary>
    /// File present and parseable but the recorded host fingerprint
    /// disagrees with the live host. Persisted curves were discarded.
    /// </summary>
    public static readonly LoadResult FingerprintMismatch = new(LoadStatus.FingerprintMismatch);
}

/// <summary>Categorical result of a load attempt.</summary>
public enum LoadStatus
{
    /// <summary>Curves successfully rehydrated. See <see cref="LoadResult.LoadedCount"/>.</summary>
    Loaded,

    /// <summary>No calibration file existed at the configured path.</summary>
    NoFile,

    /// <summary>File existed but couldn't be parsed.</summary>
    ParseError,

    /// <summary>File parsed but the host fingerprint didn't match.</summary>
    FingerprintMismatch,
}

// ─────────────────────────── JSON DTOs ───────────────────────────

internal sealed class CalibrationFileDto
{
    [JsonPropertyName("host")] public HostFingerprintDto? Host { get; set; }
    [JsonPropertyName("models")] public Dictionary<string, ModelCalibrationDto>? Models { get; set; }
}

internal sealed class HostFingerprintDto
{
    [JsonPropertyName("gpu_uuid")] public string GpuUuid { get; set; } = "";
    [JsonPropertyName("vram_total_bytes")] public long VramTotalBytes { get; set; }
    [JsonPropertyName("driver_version")] public string DriverVersion { get; set; } = "";
    [JsonPropertyName("ort_version")] public string OrtVersion { get; set; } = "";
}

internal sealed class ModelCalibrationDto
{
    [JsonPropertyName("file_sha256")] public string? FileSha256 { get; set; }
    [JsonPropertyName("weight_cost_bytes")] public long WeightCostBytes { get; set; }
    [JsonPropertyName("curve")] public Dictionary<string, CalibrationEntryDto>? Curve { get; set; }
}

internal sealed class CalibrationEntryDto
{
    [JsonPropertyName("total_vram_bytes")] public long TotalVramBytes { get; set; }
    [JsonPropertyName("observation_count")] public int ObservationCount { get; set; }
    [JsonPropertyName("last_validated_at")] public string LastValidatedAt { get; set; } = "";
}

[JsonSerializable(typeof(CalibrationFileDto))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CalibrationJsonContext : JsonSerializerContext
{
}
