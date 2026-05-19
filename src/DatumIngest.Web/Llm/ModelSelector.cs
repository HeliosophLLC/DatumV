using Heliosoph.DatumV.Models;

namespace Heliosoph.DatumV.Web.Llm;

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

    // Returns the selection, or null when no installed LLM fits. When
    // `preferred` is non-null and that model is installed and fits, it
    // wins regardless of size; otherwise the largest-fits-the-budget rule
    // applies. Pass null for `preferred` to skip the override path.
    public static ModelSelection? TrySelect(ModelCatalog catalog, string? preferred = null)
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
            if (!IsFilePresent(entry, catalog.PathResolver)) continue;

            long estimated = entry.EstimatedVramBytes
                ?? EstimateFromFile(entry, catalog.PathResolver);
            if (estimated == 0) continue;
            if (estimated > usableBudget) continue;

            candidates.Add(new Candidate(entry, estimated));
        }

        if (candidates.Count == 0) return null;

        // User-pinned model wins when it's in the candidate set. A pinned
        // name that doesn't match any installed-and-fitting LLM silently
        // falls through to the auto-pick — the picker UI gates "uninstall
        // the selected model" upstream, so this is a defensive case.
        if (!string.IsNullOrEmpty(preferred))
        {
            foreach (Candidate c in candidates)
            {
                if (string.Equals(c.Entry.Name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return new ModelSelection(c.Entry, c.EstimatedBytes, usableBudget);
                }
            }
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

    // Lists every catalog entry that's both an LLM and present on disk —
    // i.e. every model the picker can offer. Used by /api/llm/available
    // to populate the Settings dropdown. Returned in the same order
    // TrySelect would prefer (largest first, then alphabetical), so
    // "auto" UI text can show the top entry as the projected pick.
    public static IReadOnlyList<InstalledLlm> ListInstalled(ModelCatalog catalog)
    {
        long budget = catalog.VramBudgetBytes;
        long usableBudget = budget == ModelResidencyManager.UnlimitedBudget
            ? long.MaxValue
            : Math.Max(0, budget - ChatHeadroomBytes);

        List<InstalledLlm> results = new();
        foreach (KeyValuePair<string, ModelCatalogEntry> kv in catalog.Entries)
        {
            ModelCatalogEntry entry = kv.Value;
            if (!IsLlm(entry)) continue;
            if (!IsFilePresent(entry, catalog.PathResolver)) continue;

            long estimated = entry.EstimatedVramBytes
                ?? EstimateFromFile(entry, catalog.PathResolver);
            bool fits = estimated > 0 && estimated <= usableBudget;
            results.Add(new InstalledLlm(
                Name: entry.Name,
                DisplayName: entry.DisplayName ?? entry.Name,
                EstimatedVramBytes: estimated,
                FitsInBudget: fits));
        }

        results.Sort((a, b) =>
        {
            int byBytes = b.EstimatedVramBytes.CompareTo(a.EstimatedVramBytes);
            return byBytes != 0 ? byBytes : string.CompareOrdinal(a.Name, b.Name);
        });
        return results;
    }

    private static bool IsLlm(ModelCatalogEntry entry)
        => string.Equals(entry.Category, "llm", StringComparison.OrdinalIgnoreCase);

    private static bool IsFilePresent(ModelCatalogEntry entry, Heliosoph.DatumV.ModelLibrary.IModelPathResolver paths)
    {
        // RelativePath / Files are id-prefixed under the catalog substrate
        // (e.g. "llama-3.1-8b-instruct-gguf/...gguf"); route through the
        // resolver so the per-version folder layout is honoured.
        if (entry.Files is { Count: > 0 })
        {
            foreach (string rel in entry.Files)
            {
                if (!File.Exists(paths.ResolveIdPrefixedPath(rel))) return false;
            }
            return true;
        }
        if (entry.RelativePath is null) return false;
        return File.Exists(paths.ResolveIdPrefixedPath(entry.RelativePath));
    }

    private static long EstimateFromFile(ModelCatalogEntry entry, Heliosoph.DatumV.ModelLibrary.IModelPathResolver paths)
    {
        long total = 0;
        if (entry.Files is { Count: > 0 })
        {
            foreach (string rel in entry.Files)
            {
                string p = paths.ResolveIdPrefixedPath(rel);
                if (File.Exists(p)) total += new FileInfo(p).Length;
            }
        }
        else if (entry.RelativePath is not null)
        {
            string p = paths.ResolveIdPrefixedPath(entry.RelativePath);
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

public sealed record InstalledLlm(
    string Name,
    string DisplayName,
    long EstimatedVramBytes,
    bool FitsInBudget);

// Thrown by LlmDriverHolder when no installed LLM satisfies the VRAM
// budget. The hub surfaces the Message verbatim and the client checks for
// a specific marker substring to switch the chat surface into the
// "install an LLM" empty state instead of the generic error banner.
public sealed class NoLlmInstalledException : InvalidOperationException
{
    public const string Marker = "NoLlmInstalled";

    public NoLlmInstalledException(string detail)
        : base($"{Marker}: {detail}")
    {
    }
}
