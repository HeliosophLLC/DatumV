using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;

namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// Reads <c>kind: "python"</c> entries from a <see cref="CatalogManifest"/>
/// and registers them as <see cref="ModelCatalogEntry"/>s in a
/// <see cref="ModelCatalog"/>. Invoked from <see cref="ModelHost.AttachTo"/>
/// once the manifest is loaded — every Python-backed model flows through
/// the same data-driven path as the rest of the model zoo (ONNX entries
/// via their per-entry installSql, Python entries via this registrar).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy construction.</strong> Registration is cheap — it adds a
/// catalog entry whose <c>Loader</c> closure builds the
/// <see cref="PythonBackedModel"/> on first use. No subprocess is
/// spawned, no venv is materialised at registration time. The first
/// query that touches the model triggers
/// <see cref="IPythonEnvironmentManager.EnsureVenvAsync"/>, which
/// fast-paths when the venv is already set up (typical case after an
/// explicit Install) and bootstraps it otherwise (rare query-without-
/// prior-install case, behaviourally identical to the previous
/// hardcoded path).
/// </para>
/// <para>
/// <strong>Name mapping.</strong> Catalog ids may contain hyphens
/// (<c>bark-small</c>); SQL identifiers cannot. The registered model
/// name swaps hyphens for underscores (<c>bark_small</c>), matching
/// the existing convention from the hardcoded registrations.
/// </para>
/// </remarks>
public static class CatalogDrivenPythonRegistrar
{
    /// <summary>
    /// Walks <paramref name="manifest"/> and registers a
    /// <see cref="ModelCatalogEntry"/> for every <c>kind: "python"</c>
    /// model. Idempotent at the <see cref="ModelCatalog"/> level — calling
    /// twice with the same manifest throws on the second registration
    /// since <see cref="ModelCatalog.Register"/> rejects duplicate names.
    /// Hosts call this once at startup after the hardcoded ONNX
    /// registrations.
    /// </summary>
    /// <param name="catalog">Target catalog.</param>
    /// <param name="pythonEnvironments">Engine-managed Python toolchain — supplies the venv-scoped interpreter at load time.</param>
    /// <param name="manifest">Loaded <see cref="CatalogManifest"/>; the registrar reads <c>kind: "python"</c> entries and their <c>python</c> blocks.</param>
    /// <param name="licenses">Central license registry; used to resolve each entry's first <c>licenseId</c> to an SPDX label for the registered <see cref="ModelCatalogEntry"/>.</param>
    /// <param name="scriptsDirectory">Engine-bundled <c>python/</c> directory. The catalog's <c>workerScript</c> field is a filename relative to this directory.</param>
    public static void RegisterAll(
        ModelCatalog catalog,
        IPythonEnvironmentManager pythonEnvironments,
        CatalogManifest manifest,
        ILicenseRegistry licenses,
        string scriptsDirectory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pythonEnvironments);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(licenses);
        ArgumentException.ThrowIfNullOrEmpty(scriptsDirectory);

        foreach (CatalogEntry entry in manifest.Entries)
        {
            foreach (CatalogVariant variant in entry.Variants)
            {
                if (!string.Equals(variant.Kind, "python", StringComparison.OrdinalIgnoreCase)) continue;
                // ManifestStore.ValidateEntries guarantees Python != null when
                // Kind == "python"; defensive null-skip here keeps a corrupt
                // in-memory manifest from crashing registration.
                if (variant.Python is null) continue;
                // Placeholder variants point at not-yet-uploaded repos. The
                // download path also refuses them; skip at registration too
                // so the variant doesn't sit in the catalog as "registered but
                // unusable."
                if (variant.Placeholder) continue;
                RegisterOne(catalog, pythonEnvironments, entry, variant, scriptsDirectory, licenses);
            }
        }
    }

    private static void RegisterOne(
        ModelCatalog catalog,
        IPythonEnvironmentManager pythonEnvironments,
        CatalogEntry entry,
        CatalogVariant variant,
        string scriptsDirectory,
        ILicenseRegistry licenses)
    {
        CatalogPythonSpec spec = variant.Python!;
        string modelName = variant.Id.Replace('-', '_');
        string scriptPath = Path.Combine(scriptsDirectory, spec.WorkerScript);

        IReadOnlyList<DataKind> inputKinds = ParseKindList(
            spec.Signature.InputKinds, $"{variant.Id} python.signature.inputKinds");
        DataKind outputKind = ParseKind(
            spec.Signature.OutputKind, $"{variant.Id} python.signature.outputKind");
        IReadOnlyList<DataKind>? optionalArgKinds = spec.Signature.OptionalArgKinds is null
            ? null
            : ParseKindList(spec.Signature.OptionalArgKinds, $"{variant.Id} python.signature.optionalArgKinds");

        // Resolve the first license to a display string. License metadata
        // lives at entry level (every variant shares).
        string? license = entry.LicenseIds.Count > 0
            ? licenses.GetMetadata(entry.LicenseIds[0])?.Spdx
            : null;

        IModel Loader(ModelLoadContext ctx) => new PythonBackedModel(
            name: modelName,
            inputKinds: inputKinds,
            outputKind: outputKind,
            isDeterministic: spec.Signature.IsDeterministic,
            environments: pythonEnvironments,
            venvName: variant.Id,
            pythonVersion: spec.PythonVersion,
            requirements: spec.Requirements,
            scriptPath: scriptPath,
            scriptArgs: spec.ScaffoldArgs,
            readyTimeout: null,
            preferredBatchSize: 1,
            modelDirectory: ctx.Paths.GetModelRoot(variant.Id));

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "python",
            RelativePath: null,
            InputKinds: inputKinds,
            OutputKind: outputKind,
            IsDeterministic: spec.Signature.IsDeterministic,
            Loader: Loader,
            OptionalArgKinds: optionalArgKinds,
            DisplayName: variant.DisplayName,
            License: license,
            SourceUrl: null,
            Category: entry.Tasks[0],
            Files: []));
    }

    private static IReadOnlyList<DataKind> ParseKindList(IReadOnlyList<string> names, string context)
    {
        DataKind[] result = new DataKind[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            result[i] = ParseKind(names[i], $"{context}[{i}]");
        }
        return result;
    }

    private static DataKind ParseKind(string name, string context)
    {
        if (!Enum.TryParse<DataKind>(name, ignoreCase: true, out DataKind value))
        {
            throw new ArgumentException(
                $"Unknown DataKind '{name}' at {context}. "
                + $"Valid values: {string.Join(", ", Enum.GetNames<DataKind>())}.");
        }
        return value;
    }
}
