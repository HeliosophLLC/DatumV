// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using Heliosoph.DatumV.Catalog.Registries;

namespace Heliosoph.DatumV.ModelLibrary;

// Reverse identifier→catalog index built at catalog-load time from each
// version's declared `models[]` arrays. The same source of truth that
// `system.models`, `system.tasks`, pre-flight, completion, and the
// install-time cross-check all consult — built once rather than
// re-walking the manifest at every consumption site.
//
// Built once per catalog load. Holds no residency state — residency lives
// in `ModelCatalog` + `ModelRegistry` and gets joined on demand. This
// keeps the vocabulary surface immutable; install/uninstall churn doesn't
// invalidate the index.
public interface ICatalogVocabulary
{
    // Every SQL-visible model identifier the catalog declares across all
    // entries × all variants × all versions. Keyed by the snake_case
    // identifier; values carry the owning entry + variant, the version
    // that declared the identifier, and the materialised pinnedAs name.
    IReadOnlyDictionary<string, CatalogVocabularyEntry> ByIdentifier { get; }

    // Inverse of <see cref="ByIdentifier"/> keyed on the catalog variant
    // id (kebab-case install handle). Lets callers go variant-id →
    // declared identifier set without walking versions[].
    IReadOnlyDictionary<string, IReadOnlySet<string>> IdentifiersByVariant { get; }

    // Reverse index over every materialised `pinnedAs` name. The parser
    // maps `models.foo@<digits>` to the (entry × variant × version × bare
    // identifier) record by looking up the suffixed form here.
    IReadOnlyDictionary<string, CatalogPinnedReference> ByPinnedAs { get; }

    // All catalog-declared candidates implementing a given task contract.
    // One entry per (catalog entry declaring the contract × every
    // identifier the entry's variants declare). Used by the
    // `system.tasks` virtual table and by pre-flight's "alternative
    // candidates" hint. Returns an empty list when the contract has no
    // implementations declared.
    IReadOnlyList<CatalogTaskCandidate> CandidatesForTask(string contractName);
}

// One identifier known to the catalog. Carries enough context for
// pre-flight and `system.models` row materialisation without re-walking
// the manifest.
public sealed record CatalogVocabularyEntry(
    // The SQL-visible snake_case identifier.
    string Identifier,
    // Parent catalog variant id (kebab-case install handle).
    string VariantId,
    // The owning catalog entry, kept by reference so consumers can read
    // `Attributions`, `LicenseIds`, etc. without a second lookup.
    CatalogEntry OwnerEntry,
    // The owning variant, kept by reference so consumers can read
    // `Hardware`, `Versions`, etc.
    CatalogVariant OwnerVariant,
    // Every version of the owning variant that declares this identifier,
    // ordered newest-first. Always non-empty.
    IReadOnlyList<CatalogVocabularyVersion> Versions);

public sealed record CatalogVocabularyVersion(
    string VersionString,
    CatalogVersion Version,
    string PinnedAs);

public sealed record CatalogPinnedReference(
    string PinnedAs,
    string Identifier,
    CatalogVocabularyEntry Entry,
    CatalogVocabularyVersion Version);

public sealed record CatalogTaskCandidate(
    string Task,
    string ModelIdentifier,
    string VariantId);

internal sealed class CatalogVocabulary : ICatalogVocabulary
{
    public IReadOnlyDictionary<string, CatalogVocabularyEntry> ByIdentifier { get; }
    public IReadOnlyDictionary<string, IReadOnlySet<string>> IdentifiersByVariant { get; }
    public IReadOnlyDictionary<string, CatalogPinnedReference> ByPinnedAs { get; }

    private readonly IReadOnlyDictionary<string, IReadOnlyList<CatalogTaskCandidate>> _candidatesByTask;

    public CatalogVocabulary(CatalogManifest manifest)
    {
        Dictionary<string, List<CatalogVocabularyVersion>> versionsByIdentifier =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (CatalogEntry Entry, CatalogVariant Variant)> ownerByIdentifier =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> identifiersByVariant =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogEntry entry in manifest.Entries)
        {
            foreach (CatalogVariant variant in entry.Variants)
            {
                HashSet<string> identsForThisVariant = new(StringComparer.OrdinalIgnoreCase);
                foreach (CatalogVersion version in variant.Versions)
                {
                    if (version.Models is null) { continue; }
                    foreach (CatalogVersionModel vm in version.Models)
                    {
                        string pinnedAs = vm.EffectivePinnedAs(version.Version);
                        if (!versionsByIdentifier.TryGetValue(vm.Identifier, out List<CatalogVocabularyVersion>? list))
                        {
                            list = [];
                            versionsByIdentifier[vm.Identifier] = list;
                            ownerByIdentifier[vm.Identifier] = (entry, variant);
                        }
                        list.Add(new CatalogVocabularyVersion(version.Version, version, pinnedAs));
                        identsForThisVariant.Add(vm.Identifier);
                    }
                }
                if (identsForThisVariant.Count > 0)
                {
                    identifiersByVariant[variant.Id] = identsForThisVariant;
                }
            }
        }

        Dictionary<string, CatalogVocabularyEntry> byIdentifier = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string identifier, List<CatalogVocabularyVersion> versions) in versionsByIdentifier)
        {
            (CatalogEntry entry, CatalogVariant variant) = ownerByIdentifier[identifier];
            byIdentifier[identifier] = new CatalogVocabularyEntry(
                identifier,
                variant.Id,
                entry,
                variant,
                versions);
        }
        ByIdentifier = byIdentifier;

        Dictionary<string, CatalogPinnedReference> byPinnedAs = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogVocabularyEntry voc in byIdentifier.Values)
        {
            foreach (CatalogVocabularyVersion vv in voc.Versions)
            {
                byPinnedAs[vv.PinnedAs] = new CatalogPinnedReference(
                    vv.PinnedAs, voc.Identifier, voc, vv);
            }
        }
        ByPinnedAs = byPinnedAs;
        IdentifiersByVariant = identifiersByVariant.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        // Build the (task × candidate identifier) join. Tasks live at
        // entry level (every variant shares the entry's task set); each
        // variant contributes its declared identifiers as candidates.
        Dictionary<string, List<CatalogTaskCandidate>> candidatesByTask = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry entry in manifest.Entries)
        {
            if (entry.Tasks is null || entry.Tasks.Count == 0) { continue; }
            foreach (CatalogVariant variant in entry.Variants)
            {
                if (!identifiersByVariant.TryGetValue(variant.Id, out HashSet<string>? identifiers))
                {
                    continue;
                }
                foreach (string contract in entry.Tasks)
                {
                    if (TaskTypeRegistry.TryGet(contract) is null) { continue; }
                    if (!candidatesByTask.TryGetValue(contract, out List<CatalogTaskCandidate>? list))
                    {
                        list = [];
                        candidatesByTask[contract] = list;
                    }
                    foreach (string identifier in identifiers)
                    {
                        list.Add(new CatalogTaskCandidate(contract, identifier, variant.Id));
                    }
                }
            }
        }
        _candidatesByTask = candidatesByTask.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<CatalogTaskCandidate>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CatalogTaskCandidate> CandidatesForTask(string contractName)
    {
        return _candidatesByTask.TryGetValue(contractName, out IReadOnlyList<CatalogTaskCandidate>? list)
            ? list
            : [];
    }
}
