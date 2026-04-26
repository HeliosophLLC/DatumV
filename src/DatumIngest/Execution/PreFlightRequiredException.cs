namespace DatumIngest.Execution;

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
        int suggestions = r.Suggestions.Count;
        if (models == 0 && suggestions > 0)
        {
            // Typo-only case: surface the first suggestion so CLI / tests
            // without pre-flight projection still get a readable hint.
            PreFlightSuggestion first = r.Suggestions[0];
            string tail = suggestions == 1
                ? string.Empty
                : $" (+{suggestions - 1} more)";
            return $"Unknown function '{first.TypedName}'. Did you mean '{first.Suggestion}'?{tail}";
        }
        if (models > 0 && suggestions == 0)
        {
            PreFlightModelRequirement first = r.Models[0];
            string tail = models == 1 ? string.Empty : $" (+{models - 1} more)";
            return $"Model '{first.TypedReference}' is not installed.{tail}";
        }
        return $"Pre-flight blocked: {models} model(s) need install, {suggestions} likely typo(s).";
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
    string? VersionDeprecationReason);

/// <summary>
/// One likely-typo function reference the user wrote. The UI shows it as
/// "did you mean …?" instead of executing the query.
/// </summary>
public sealed record PreFlightSuggestion(
    string TypedName,
    string Suggestion);

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
    /// catalog version (some entry declared a <see cref="DatumIngest.ModelLibrary.CatalogVersionModel.PinnedAs"/>
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
