using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Thrown by the parse-time pre-flight pass when a top-level query
/// references catalog models that aren't ready to execute (model not
/// installed, pinned version not on disk, pinned version unknown to the
/// catalog) or contains likely typos against the known function /
/// identifier surface. Carries a <see cref="PreFlightRequirements"/>
/// payload the host projects to the client (e.g. an NDJSON
/// <c>preflight_required</c> event over the query-stream wire) so the
/// UI can offer install / typo-fix flows before any operator is built.
/// </summary>
/// <remarks>
/// <para>
/// Subclasses <see cref="ExecutionException"/> so the standard
/// "user-actionable vs internal error" catch boundary surfaces the
/// message correctly when no host-specific projection is wired (CLI,
/// tests). Hosts that understand pre-flight catch the typed exception
/// explicitly and emit the structured payload.
/// </para>
/// <para>
/// Pre-flight runs in <c>TableCatalog.PlanQuery</c> between
/// <c>NamedArgPermuter</c> and <c>UdfInliner</c> — the AST is already
/// canonicalised but UDF bodies remain opaque, so the walk sees only
/// model references the user wrote directly.
/// </para>
/// </remarks>
public sealed class PreFlightRequiredException : ExecutionException
{
    /// <summary>The structured pre-flight payload the host projects to the client.</summary>
    public PreFlightRequirements Requirements { get; }

    /// <summary>Creates the exception carrying the pre-flight payload.</summary>
    public PreFlightRequiredException(PreFlightRequirements requirements)
        : base(BuildMessage(requirements))
    {
        Requirements = requirements;
    }

    private static string BuildMessage(PreFlightRequirements r)
    {
        int models = r.Models.Count;
        int datasets = r.Datasets.Count;
        int suggestions = r.Suggestions.Count;
        if (models == 0 && datasets == 0 && suggestions > 0)
        {
            // Typo-only case: surface the first suggestion so CLI / tests
            // without pre-flight projection still get a readable hint.
            PreFlightSuggestion first = r.Suggestions[0];
            string tail = suggestions == 1
                ? string.Empty
                : $" (+{suggestions - 1} more)";
            return $"Unknown function '{first.TypedName}'. Did you mean '{first.Suggestion}'?{tail}";
        }
        if (models > 0 && datasets == 0 && suggestions == 0)
        {
            PreFlightModelRequirement first = r.Models[0];
            string tail = models == 1 ? string.Empty : $" (+{models - 1} more)";
            return $"Model '{first.TypedReference}' is not installed.{tail}";
        }
        if (models == 0 && datasets > 0 && suggestions == 0)
        {
            PreFlightDatasetRequirement first = r.Datasets[0];
            string tail = datasets == 1 ? string.Empty : $" (+{datasets - 1} more)";
            return $"Dataset '{first.TypedReference}' is not installed.{tail}";
        }
        return
            $"Pre-flight blocked: {models} model(s) need install, " +
            $"{datasets} dataset(s) need install, {suggestions} likely typo(s).";
    }
}

/// <summary>
/// Structured pre-flight result. <see cref="Models"/> lists every
/// catalog-known reference whose install isn't ready; <see cref="Suggestions"/>
/// lists likely-typo function names the user wrote. A non-empty payload
/// blocks plan construction.
/// </summary>
public sealed record PreFlightRequirements(
    IReadOnlyList<PreFlightModelRequirement> Models,
    IReadOnlyList<PreFlightDatasetRequirement> Datasets,
    IReadOnlyList<PreFlightSuggestion> Suggestions);

/// <summary>
/// One catalog-model reference the user wrote that isn't ready to execute.
/// </summary>
public sealed record PreFlightModelRequirement(
    // The reference as the user typed it: "depth_anything_v3_large_meters"
    // or "depth_anything_v3_large_meters@20260529". Used by the UI to
    // round-trip the install action back to the originating call site.
    string TypedReference,
    // The bare SQL-visible identifier (no @<version> suffix).
    string Identifier,
    // Parent catalog entry id (kebab-case). What the downloader installs.
    string CatalogEntryId,
    // Version string the install will fetch. For bare references this is
    // the catalog's recommended version (Versions[0]); for pinned
    // references it's the pinned version. Null when
    // <see cref="Reason"/> is <see cref="PreFlightReason.PinnedVersionUnknown"/>
    // (no catalog version matches the pin so we can't recover automatically).
    string? Version,
    // True when the user wrote `@<version>` on the reference.
    bool VersionPinned,
    PreFlightReason Reason,
    // Catalog entry's approximate on-disk size in MiB, surfaced so the
    // install modal can show "this will download ~990 MB."
    int? ApproxSizeMb,
    // Sibling identifiers the same catalog version registers — surfaced
    // so the user sees that installing one entry lights up a family of
    // names (the catalog often declares multiple identifiers per cut).
    IReadOnlyList<string> SiblingIdentifiers,
    bool EntryDeprecated,
    string? SupersededBy,
    bool VersionDeprecated,
    string? VersionDeprecationReason,
    // License ids the user must accept before the catalog entry can be
    // installed. Empty for entries with no licenseIds or whose licenses
    // all carry <c>requiresAcceptance=false</c>. Mirrors
    // <see cref="Heliosoph.DatumV.ModelLibrary.CatalogModel.LicenseIds"/>;
    // pre-flight surfaces them so the install modal can prompt acceptance
    // up front instead of letting the install-time 412 path open separate
    // dialogs after the user clicked Install.
    IReadOnlyList<string> LicenseIds);

/// <summary>
/// One dataset reference the user wrote whose variant isn't installed yet.
/// Symmetrical to <see cref="PreFlightModelRequirement"/> for the
/// <c>datasets.X</c> table-source surface — the UI offers an install
/// flow before any operator is built.
/// </summary>
public sealed record PreFlightDatasetRequirement(
    // The reference as the user typed it: "datasets.coco_test2017".
    string TypedReference,
    // The bound table name (no schema prefix). For single-job variants
    // this is the variant id verbatim; for multi-job variants it's
    // `<variantId>_<tableName>`.
    string Identifier,
    // The install handle the downloader keys on (the variant id).
    string VariantId,
    // Parent entry's user-facing name (e.g. "COCO 2017").
    string EntryName,
    // Variant subtitle (e.g. "test2017 (images)") for the install modal.
    string DisplayName,
    // Recommended catalog version (Versions[0].Version).
    string Version,
    // Approximate archive bytes for "this will download ~6.6 GB."
    long ApproxArchiveBytes,
    // License ids the user must accept before install. Empty when the
    // entry has no licenseIds or all of them carry requiresAcceptance=false.
    IReadOnlyList<string> LicenseIds);

/// <summary>
/// One likely-typo function reference the user wrote. The UI shows it as
/// "did you mean …?" instead of executing the query.
/// </summary>
public sealed record PreFlightSuggestion(
    string TypedName,
    string Suggestion);

/// <summary>
/// Read-only window into the dataset catalog used by the pre-flight pass.
/// Implemented by the dataset binder so PreFlight can resolve
/// <c>&lt;schema&gt;.&lt;table&gt;</c> table references against the dataset
/// manifest without coupling the execution layer to DatasetLibrary types
/// directly. Hosts that don't ship a dataset surface pass
/// <see langword="null"/>.
/// </summary>
public interface IPreFlightDatasetSource
{
    /// <summary>True when <paramref name="schema"/> is a schema the dataset
    /// manifest binds tables into.</summary>
    bool IsDatasetSchema(string schema);

    /// <summary>
    /// Tries to describe the table at <c>(schema, name)</c>. Returns true
    /// with a populated candidate when the manifest knows the variant
    /// (whether installed or not); false otherwise (typo / unmounted /
    /// wrong schema). The candidate carries
    /// <see cref="PreFlightDatasetCandidate.IsInstalled"/> so the walker
    /// only emits a requirement when the install state warrants it.
    /// </summary>
    bool TryDescribe(
        string schema,
        string name,
        [NotNullWhen(true)] out PreFlightDatasetCandidate? candidate);
}

/// <summary>
/// Manifest-derived description of a dataset table the walker is
/// asking about. Carries the install-modal payload (entry / display
/// name / version / size / license) plus the install state so the
/// walker can decide whether to emit a requirement.
/// </summary>
public sealed record PreFlightDatasetCandidate(
    string VariantId,
    string EntryName,
    string DisplayName,
    string Version,
    long ApproxArchiveBytes,
    IReadOnlyList<string> LicenseIds,
    bool IsInstalled);

/// <summary>
/// Why pre-flight blocked a particular model reference.
/// </summary>
public enum PreFlightReason
{
    /// <summary>
    /// Bare <c>models.X</c> where X is declared in the catalog but no
    /// active install pointer exists for the entry yet. Recovery is
    /// the install-from-modal flow against the recommended version.
    /// </summary>
    ModelNotInstalled,

    /// <summary>
    /// <c>models.X@&lt;version&gt;</c> where the pin maps to a known
    /// catalog version (some entry declared a <see cref="Heliosoph.DatumV.ModelLibrary.CatalogVersionModel.PinnedAs"/>
    /// matching the typed suffix) but the version's folder is not on
    /// disk. Recovery is "install that specific version alongside
    /// whatever's active" via the pinned-install path.
    /// </summary>
    PinnedVersionNotInstalled,

    /// <summary>
    /// <c>models.X@&lt;version&gt;</c> where no catalog version maps to
    /// the typed suffix. The user pinned a cut the catalog doesn't know
    /// about; recovery is "pin to a known version" (UI surfaces the
    /// available versions). No automatic install path.
    /// </summary>
    PinnedVersionUnknown,
}
