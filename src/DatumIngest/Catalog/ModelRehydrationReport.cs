namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Outcome of <see cref="TableCatalog.RehydrateModelsAsync"/>. Hosts log
/// this on startup so a regression in model loading (missing ONNX file,
/// dispatcher backend gone, parse error from a manual catalog edit)
/// surfaces visibly instead of silently dropping the model.
/// </summary>
/// <param name="Loaded">Number of persisted model entries that were
/// successfully re-applied. Each one is now registered in
/// <see cref="Registries.ModelRegistry"/> with fresh bound sessions.</param>
/// <param name="Skipped">Number of entries that couldn't be rehydrated.
/// Reasons appear in <paramref name="Warnings"/>; the skipped entries
/// remain in the catalog file and will be retried on the next startup.</param>
/// <param name="Warnings">Human-readable per-entry messages — one per
/// skipped model, plus any non-fatal observations from the rehydrate
/// loop (e.g. a model that parsed but landed in an unexpected schema).</param>
public sealed record ModelRehydrationReport(
    int Loaded,
    int Skipped,
    IReadOnlyList<string> Warnings);
