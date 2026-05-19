using Heliosoph.DatumV.ModelLibrary;

namespace Heliosoph.DatumV.Tests.Support;

/// <summary>
/// In-memory <see cref="ILicenseRegistry"/> for tests that fabricate
/// synthetic <see cref="CatalogManifest"/> / <see cref="DatasetCatalogManifest"/>
/// instances. Pre-loaded with the same vocabulary the real
/// <c>licenses/index.json</c> ships (mit, apache-2.0, cc-by-4.0,
/// bsd-2-clause, bsd-3-clause, creativeml-openrail-m,
/// creativeml-openrail-pp-m, stability-ai-community,
/// llama-3.1-community, falcon-llm-license-2.0) so tests authored
/// against any of those ids don't need to instantiate their own.
/// </summary>
public sealed class TestLicenseRegistry : ILicenseRegistry
{
    public static TestLicenseRegistry Instance { get; } = new();

    private static readonly Dictionary<string, CatalogLicense> _licenses =
        new(StringComparer.Ordinal)
        {
            ["mit"] = Synthesize("mit", "MIT", requiresAcceptance: false),
            ["apache-2.0"] = Synthesize("apache-2.0", "Apache-2.0", requiresAcceptance: false),
            ["cc-by-4.0"] = Synthesize("cc-by-4.0", "CC-BY-4.0", requiresAcceptance: false),
            ["bsd-2-clause"] = Synthesize("bsd-2-clause", "BSD-2-Clause", requiresAcceptance: false),
            ["bsd-3-clause"] = Synthesize("bsd-3-clause", "BSD-3-Clause", requiresAcceptance: false),
            ["creativeml-openrail-m"] = Synthesize("creativeml-openrail-m", "CreativeML-OpenRAIL-M", requiresAcceptance: true),
            ["creativeml-openrail-pp-m"] = Synthesize("creativeml-openrail-pp-m", "CreativeML-OpenRAIL-Plus-Plus-M", requiresAcceptance: true),
            ["openrail-pp"] = Synthesize("openrail-pp", "OpenRAIL-PP", requiresAcceptance: true),
            ["stability-ai-community"] = Synthesize("stability-ai-community", "Stability-AI-Community", requiresAcceptance: true),
            ["llama-3.1-community"] = Synthesize("llama-3.1-community", "Llama-3.1-Community", requiresAcceptance: true),
            ["falcon-llm-license-2.0"] = Synthesize("falcon-llm-license-2.0", "Falcon-LLM-2.0", requiresAcceptance: true),
            ["test-license"] = Synthesize("test-license", "TestLicense", requiresAcceptance: false),
        };

    public IReadOnlyDictionary<string, CatalogLicense> All => _licenses;

    public CatalogLicense? GetMetadata(string licenseId)
        => _licenses.TryGetValue(licenseId, out CatalogLicense? meta) ? meta : null;

    public string? GetText(string licenseId)
        => _licenses.ContainsKey(licenseId) ? $"[synthetic text for {licenseId}]" : null;

    private static CatalogLicense Synthesize(string id, string spdx, bool requiresAcceptance)
        => new(
            Title: spdx,
            Spdx: spdx,
            CanonicalUrl: $"https://example.invalid/{id}",
            TextFile: $"{id}.txt",
            Summary: "Synthetic test license.",
            RequiresAcceptance: requiresAcceptance);
}
