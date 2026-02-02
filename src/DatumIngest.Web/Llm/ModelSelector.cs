using DatumIngest.Models;

namespace DatumIngest.Web.Llm;

// Picks an LLM from the ModelCatalog that fits the residency manager's VRAM
// budget. v1 strategy: filter to LLMs whose files are present on disk, pick
// the one with the largest estimated VRAM footprint that still fits.
//
// "Continuity" — preferring whatever model the last assistant turn used —
// is filed for a later round. For v1 the selector is stateless: same inputs,
// same answer.
internal static class ModelSelector
{
    // Default headroom held back from the VRAM budget at selection time. The
    // residency manager already subtracts a 4 GiB headroom inside
    // VramBudgetResolver.Resolve, but the chat-shaped use case wants
    // additional room for KV cache growth as conversations lengthen. This
    // is on top of the resolver's headroom — empirically 2 GiB extra is
    // enough for an 8K-context Qwen 7B chat to run a long session without
    // bumping into the limit.
    public const long ChatHeadroomBytes = 2L * 1024 * 1024 * 1024;

    public static ModelSelection Select(ModelCatalog catalog)
    {
        long budget = catalog.VramBudgetBytes;
        long usableBudget = budget == ModelResidencyManager.UnlimitedBudget
            ? long.MaxValue
            : Math.Max(0, budget - ChatHeadroomBytes);

        List<Candidate> candidates = new();
        foreach (KeyValuePair<string, ModelCatalogEntry> kv in catalog.Entries)
        {
            ModelCatalogEntry entry = kv.Value;
            if (!IsLlm(entry)) continue;
            if (!IsFilePresent(entry, catalog.ModelDirectory)) continue;

            long estimated = entry.EstimatedVramBytes
                ?? EstimateFromFile(entry, catalog.ModelDirectory);
            if (estimated == 0) continue;
            if (estimated > usableBudget) continue;

            candidates.Add(new Candidate(entry, estimated));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No LLM available that fits in {FormatBytes(usableBudget)} of usable VRAM " +
                $"(catalog budget {FormatBytes(budget)} − {FormatBytes(ChatHeadroomBytes)} chat headroom). " +
                "Either drop a smaller LLM GGUF into the models directory, or raise the VRAM budget.");
        }

        // Largest model that fits wins. Ties broken by name for determinism.
        candidates.Sort((a, b) =>
        {
            int byBytes = b.EstimatedBytes.CompareTo(a.EstimatedBytes);
            return byBytes != 0 ? byBytes : string.CompareOrdinal(a.Entry.Name, b.Entry.Name);
        });
        Candidate chosen = candidates[0];
        return new ModelSelection(chosen.Entry, chosen.EstimatedBytes, usableBudget);
    }

    private static bool IsLlm(ModelCatalogEntry entry)
        => string.Equals(entry.Category, "llm", StringComparison.OrdinalIgnoreCase);

    private static bool IsFilePresent(ModelCatalogEntry entry, string modelDirectory)
    {
        if (entry.Files is { Count: > 0 })
        {
            foreach (string rel in entry.Files)
            {
                if (!File.Exists(Path.Combine(modelDirectory, rel))) return false;
            }
            return true;
        }
        if (entry.RelativePath is null) return false;
        return File.Exists(Path.Combine(modelDirectory, entry.RelativePath));
    }

    private static long EstimateFromFile(ModelCatalogEntry entry, string modelDirectory)
    {
        long total = 0;
        if (entry.Files is { Count: > 0 })
        {
            foreach (string rel in entry.Files)
            {
                string p = Path.Combine(modelDirectory, rel);
                if (File.Exists(p)) total += new FileInfo(p).Length;
            }
        }
        else if (entry.RelativePath is not null)
        {
            string p = Path.Combine(modelDirectory, entry.RelativePath);
            if (File.Exists(p)) total = new FileInfo(p).Length;
        }
        return (long)(total * 1.2);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes == long.MaxValue) return "unbounded";
        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;
        return bytes >= GiB ? $"{bytes / GiB:F2} GiB" : $"{bytes / MiB:F1} MiB";
    }

    private readonly record struct Candidate(ModelCatalogEntry Entry, long EstimatedBytes);
}

internal sealed record ModelSelection(
    ModelCatalogEntry Entry,
    long EstimatedBytes,
    long UsableBudgetBytes);
