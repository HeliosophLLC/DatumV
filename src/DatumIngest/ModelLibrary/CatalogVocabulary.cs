// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using DatumIngest.Catalog.Registries;

namespace DatumIngest.ModelLibrary;

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
    // entries × all versions. Keyed by the snake_case identifier; values
    // carry the owning <see cref="CatalogModel"/>, the version that
    // declared the identifier, and the materialised pinnedAs name the
    // installer rewrites to under `@<version>` pin syntax.
    //
    // Multiple versions of the same entry can re-declare the same
    // identifier; the entry under <see cref="CatalogVocabularyEntry.Versions"/>
    // is ordered newest-first so consumers can walk for the latest
    // declaration. The single "preferred" entry under
    // <see cref="ByIdentifier"/>'s value is the newest declaration.
    IReadOnlyDictionary<string, CatalogVocabularyEntry> ByIdentifier { get; }

    // Inverse of <see cref="ByIdentifier"/> for the catalog-entry id
    // (kebab-case). Lets callers go entry-id → declared identifier set
    // without walking versions[]. The set is the union across all
    // versions of that entry.
    IReadOnlyDictionary<string, IReadOnlySet<string>> IdentifiersByEntry { get; }

    // Reverse index over every materialised `pinnedAs` name. The parser
    // maps `models.foo@<digits>` to the (entry × version × bare identifier)
    // record by looking up the suffixed form here. Empty when no version
    // declares any models[] array. Values reference back into the same
    // <see cref="CatalogVocabularyEntry"/> instances exposed by
    // <see cref="ByIdentifier"/> so consumers can chase identifiers,
    // versions, and owner metadata from one hit.
    IReadOnlyDictionary<string, CatalogPinnedReference> ByPinnedAs { get; }

    // All catalog-declared candidates implementing a given task contract.
    // One entry per (catalog model declaring the contract × every
    // identifier that model's versions declare). Used by the
    // `system.tasks` virtual table and by pre-flight's "alternative
    // candidates" hint. Returns an empty list when the contract has no
    // implementations declared.
    IReadOnlyList<CatalogTaskCandidate> CandidatesForTask(string contractName);
}

// One identifier known to the catalog. Carries enough context for
// pre-flight and `system.models` row materialisation without re-walking
// the manifest.
public sealed record CatalogVocabularyEntry(
    // The SQL-visible snake_case identifier. Same value as the key in
    // <see cref="ICatalogVocabulary.ByIdentifier"/>.
    string Identifier,
    // Parent catalog entry id (kebab-case).
    string CatalogEntryId,
    // The owning catalog entry, kept by reference so consumers can read
    // `Hardware`, `LicenseIds`, `Versions`, etc. without a second lookup.
    CatalogModel Owner,
    // Every version of the owning entry that declares this identifier,
    // ordered newest-first. Always non-empty (otherwise the identifier
    // wouldn't appear in the index).
    IReadOnlyList<CatalogVocabularyVersion> Versions);

// One (version × identifier) declaration within
// <see cref="CatalogVocabularyEntry.Versions"/>.
public sealed record CatalogVocabularyVersion(
    // The catalog version string (e.g. "2026-05-29").
    string VersionString,
    // The catalog-version metadata, kept by reference.
    CatalogVersion Version,
    // The materialised pinnedAs name (explicit override or convention
    // default `<identifier>@<digits-of-VersionString>`). Used by the
    // installer when rewriting CREATE OR REPLACE MODEL under
    // `@<version>` pin syntax, and by the parser to resolve
    // `models.foo@<version>` references.
    string PinnedAs);

// One hit from <see cref="ICatalogVocabulary.ByPinnedAs"/>. Resolves a
// suffixed pin form like `foo@20260529` to the (catalog entry × version
// × bare identifier) tuple the engine needs for pinned-install + pinned
// dispatch.
public sealed record CatalogPinnedReference(
    string PinnedAs,
    string Identifier,
    CatalogVocabularyEntry Entry,
    CatalogVocabularyVersion Version);

// One (taskContract × candidateModelIdentifier) pair surfaced by
// <see cref="ICatalogVocabulary.CandidatesForTask"/>. Powers the
// `system.tasks` rows and pre-flight's alternative-candidates listing.
public sealed record CatalogTaskCandidate(
    string Task,
    string ModelIdentifier,
    string CatalogEntryId);

internal sealed class CatalogVocabulary : ICatalogVocabulary
{
    public IReadOnlyDictionary<string, CatalogVocabularyEntry> ByIdentifier { get; }
    public IReadOnlyDictionary<string, IReadOnlySet<string>> IdentifiersByEntry { get; }
    public IReadOnlyDictionary<string, CatalogPinnedReference> ByPinnedAs { get; }

    private readonly IReadOnlyDictionary<string, IReadOnlyList<CatalogTaskCandidate>> _candidatesByTask;

    public CatalogVocabulary(CatalogManifest manifest)
    {
        // Walk every version's declared models[] and bucket by identifier.
        // The validation pass in ManifestStore already guarantees no
        // cross-entry identifier collisions, so a flat dictionary keyed
        // on the case-insensitive identifier is unambiguous.
        Dictionary<string, List<CatalogVocabularyVersion>> versionsByIdentifier = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CatalogModel> ownerByIdentifier = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> identifiersByEntry = new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogModel entry in manifest.Models)
        {
            HashSet<string> identsForThisEntry = new(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogVersion version in entry.Versions)
            {
                if (version.Models is null) { continue; }
                foreach (CatalogVersionModel vm in version.Models)
                {
                    string pinnedAs = vm.EffectivePinnedAs(version.Version);
                    if (!versionsByIdentifier.TryGetValue(vm.Identifier, out List<CatalogVocabularyVersion>? list))
                    {
                        list = [];
                        versionsByIdentifier[vm.Identifier] = list;
                        ownerByIdentifier[vm.Identifier] = entry;
                    }
                    list.Add(new CatalogVocabularyVersion(version.Version, version, pinnedAs));
                    identsForThisEntry.Add(vm.Identifier);
                }
            }
            if (identsForThisEntry.Count > 0)
            {
                identifiersByEntry[entry.Id] = identsForThisEntry;
            }
        }

        // Materialise the immutable record-shaped public form. The
        // version list within each entry is already newest-first
        // because catalog.json authors versions[] newest-first and we
        // walk in iteration order.
        Dictionary<string, CatalogVocabularyEntry> byIdentifier = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string identifier, List<CatalogVocabularyVersion> versions) in versionsByIdentifier)
        {
            CatalogModel owner = ownerByIdentifier[identifier];
            byIdentifier[identifier] = new CatalogVocabularyEntry(
                identifier,
                owner.Id,
                owner,
                versions);
        }
        ByIdentifier = byIdentifier;

        // Reverse index: every materialised pinnedAs → its (entry,
        // version, identifier) tuple. ManifestStore.ValidateModels
        // already guarantees global uniqueness so the dictionary is
        // unambiguous.
        Dictionary<string, CatalogPinnedReference> byPinnedAs = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogVocabularyEntry entry in byIdentifier.Values)
        {
            foreach (CatalogVocabularyVersion vv in entry.Versions)
            {
                byPinnedAs[vv.PinnedAs] = new CatalogPinnedReference(
                    vv.PinnedAs, entry.Identifier, entry, vv);
            }
        }
        ByPinnedAs = byPinnedAs;
        IdentifiersByEntry = identifiersByEntry.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        // Build the (task × candidate identifier) join. For each entry's
        // declared task contracts, every identifier the entry declares
        // is a candidate. `system.tasks` materialises one row per pair so
        // users can see every available implementation of a contract.
        Dictionary<string, List<CatalogTaskCandidate>> candidatesByTask = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel entry in manifest.Models)
        {
            if (entry.Tasks is null || entry.Tasks.Count == 0) { continue; }
            if (!identifiersByEntry.TryGetValue(entry.Id, out HashSet<string>? identifiers)) { continue; }
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
                    list.Add(new CatalogTaskCandidate(contract, identifier, entry.Id));
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
