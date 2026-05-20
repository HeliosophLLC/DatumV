using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Registries;

/// <summary>
/// A registered SQL-bodied model — a procedural function with an additional
/// <see cref="UsingPath"/> binding that names the ONNX file/bundle the body's
/// <c>infer()</c> calls dispatch through, plus the loaded
/// <see cref="IInferenceSession"/>(s) that satisfy those calls.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="UdfDescriptor"/>'s procedural shape — same parameter
/// list semantics, same body-validation rules, same <see cref="QualifiedName"/>
/// identity. Differences:
/// <list type="bullet">
///   <item><description><see cref="UsingPath"/> carries the path supplied
///   in the <c>USING</c> clause (raw — the registrar resolves it against
///   <c>modelDirectory</c> / <c>file://</c> at registration time).</description></item>
///   <item><description><see cref="BoundSessions"/> is populated by the
///   registrar after a successful <see cref="IInferenceDispatcher.LoadBundleAsync"/>;
///   <c>infer()</c> looks up the active descriptor on the evaluation
///   frame and dispatches to one of these sessions by name.</description></item>
///   <item><description><see cref="ReturnTypeName"/> is non-null (required
///   for models; the procedural body has no implicit return shape the
///   planner could infer).</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Session ownership.</strong> Replacing a descriptor (via
/// <c>CREATE OR REPLACE MODEL</c>) is the moment to dispose the previous
/// descriptor's sessions. The registry's <see cref="ModelRegistry.Register"/>
/// returns the displaced descriptor so the caller can dispose it after
/// any in-flight queries finish using the old binding.
/// </para>
/// </remarks>
/// <param name="SchemaName">Schema the model lives in.</param>
/// <param name="Name">Unqualified model name (case-insensitive).</param>
/// <param name="Parameters">Declared parameters.</param>
/// <param name="ReturnTypeName">
/// Return-type annotation from <c>RETURNS T</c>. Always non-null for models.
/// </param>
/// <param name="UsingPath">
/// Raw path supplied to the <c>USING</c> clause. The registrar resolves
/// this against the host's models directory (<c>file://</c>-prefixed paths
/// are treated as absolute) before passing to the dispatcher.
/// <see langword="null"/> for delegating models that declared no
/// <c>USING</c> clause — their body produces its result by calling into
/// another model or a UDF, with no weights of their own.
/// </param>
/// <param name="ResolvedUsingPath">
/// Absolute path after the registrar resolves <see cref="UsingPath"/>
/// against the host's models directory. Lets the body's scalar functions
/// (e.g. <c>tokenizer.encode_bert</c>) find sibling files like
/// <c>vocab.txt</c> next to the bound weights without re-implementing
/// the path-resolution rules. <see langword="null"/> when
/// <see cref="UsingPath"/> is null.
/// </param>
/// <param name="StatementBody">
/// Procedural body. Always non-null on a valid model (CREATE MODEL parser
/// rejects expression bodies).
/// </param>
/// <param name="BoundSessions">
/// Lazy session resolver covering every alias declared in the model's
/// <c>USING ... AS</c> clause. The registrar builds this at
/// <c>CREATE MODEL</c> time but defers the actual ONNX session load
/// until the first <c>infer('alias', ...)</c> call inside the body —
/// catalog rehydration on startup pays only path resolution + AST
/// parsing cost, never per-model session load. Single-session bundles
/// expose one alias <c>"default"</c>. Disposal walks only the aliases
/// that finished loading.
/// </param>
/// <param name="ReturnIsNotNull">
/// When <see langword="true"/>, the procedural executor applies a runtime
/// null-assertion to the value passed to <c>RETURN</c>.
/// </param>
/// <param name="SourceText">
/// The original <c>CREATE MODEL</c> SQL text, captured verbatim for
/// persistence and round-trip display.
/// </param>
/// <param name="ImplementsTaskName">
/// Optional <c>IMPLEMENTS TaskName</c> task contract declaration. Surfaces
/// on <c>system.models.task</c> for frontend dispatch routing.
/// </param>
/// <param name="UsingFiles">
/// Optional multi-session bundle declaration mirroring
/// <see cref="Heliosoph.DatumV.Parsing.Ast.CreateModelStatement.UsingFiles"/>.
/// When non-null, every entry's session is loaded into
/// <see cref="BoundSessions"/> keyed by its alias; the body's
/// <c>infer('alias', value)</c> calls dispatch by name. When null (legacy
/// single-session bundles) <see cref="BoundSessions"/> has one entry
/// keyed <c>"default"</c> loaded from <see cref="UsingPath"/>.
/// </param>
/// <param name="CatalogId">
/// Parent catalog entry id (kebab-case, e.g. <c>"sd-turbo"</c>) when this
/// descriptor was registered by a catalog-driven install; <see langword="null"/>
/// for user-authored <c>CREATE MODEL</c> registrations. Populated by
/// <see cref="Heliosoph.DatumV.ModelLibrary.ModelInstallContext.CurrentCatalogId"/>
/// at registration time. Persists into the catalog file's model row so
/// rehydrate can resolve the originating installSql by
/// <c>(CatalogId, CatalogVersion)</c> instead of replaying a stale snapshot
/// of the source text — edits to the on-disk SQL file flow through to the
/// next process start without catalog surgery.
/// </param>
/// <param name="CatalogVersion">
/// Version string of the catalog cut that produced this registration
/// (e.g. <c>"2026-05-29"</c>), or <see langword="null"/> when
/// <paramref name="CatalogId"/> is null. Persisted alongside
/// <paramref name="CatalogId"/>.
/// </param>
/// <param name="PinnedAs">
/// When non-null, the suffixed pinned-form identifier this descriptor
/// was registered under (e.g. <c>"foo@20260529"</c>) — meaning this row
/// is a pinned-mode install coexisting with a different bare-form active
/// version. <see langword="null"/> for bare installs and user
/// <c>CREATE MODEL</c>. Persisted on the catalog row so rehydrate knows
/// to apply the pinned-identifier rewrite when re-executing the
/// originating installSql.
/// </param>
public sealed record ModelDescriptor(
    string SchemaName,
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string ReturnTypeName,
    string? UsingPath,
    string? ResolvedUsingPath,
    IReadOnlyList<Statement> StatementBody,
    LazyModelSessions BoundSessions,
    bool ReturnIsNotNull = false,
    string? SourceText = null,
    string? ImplementsTaskName = null,
    IReadOnlyList<ResolvedUsingFile>? UsingFiles = null,
    string? CatalogId = null,
    string? CatalogVersion = null,
    string? PinnedAs = null)
{
    /// <summary>Canonical <c>(schema, name)</c> identity.</summary>
    public QualifiedName QualifiedName => new(SchemaName, Name);
}

/// <summary>
/// Runtime-resolved counterpart to <see cref="Heliosoph.DatumV.Parsing.Ast.UsingFileSpec"/>.
/// The registrar resolves each declared path against the host's models
/// directory at <c>CREATE MODEL</c> time and threads the resolved absolute
/// path here so downstream consumers (sidecar-relative file resolution,
/// session caches, status probes) don't re-walk the resolution rules.
/// </summary>
/// <param name="Path">Original path as written in the SQL.</param>
/// <param name="Alias">Session alias used by <c>infer('alias', ...)</c>.</param>
/// <param name="ResolvedPath">Absolute filesystem path after resolution.</param>
public sealed record ResolvedUsingFile(string Path, string Alias, string ResolvedPath);

/// <summary>
/// Process-scoped registry of SQL-defined models. Parallel to
/// <see cref="UdfRegistry"/> in shape — entries are keyed on
/// <see cref="QualifiedName"/>, lookup is search-path-aware — but
/// deliberately separate storage so <c>system.udfs</c> stays focused on
/// pure SQL routines and <c>system.models</c> surfaces both built-in
/// <see cref="Heliosoph.DatumV.Models.ModelCatalog"/> entries and these
/// SQL-defined ones.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Disposal semantics.</strong> The registry holds strong
/// references to descriptor sessions (via <see cref="ModelDescriptor.BoundSessions"/>).
/// <see cref="Register"/> returns any displaced descriptor so the caller
/// can dispose it; <see cref="Unregister"/> returns the removed
/// descriptor likewise. The registry itself never disposes — sessions
/// outlive at-least the in-flight queries that might still be using
/// them, and the dispose decision belongs to the registrar (which knows
/// about query lifecycles, residency, etc.).
/// </para>
/// </remarks>
public sealed class ModelRegistry
{
    private readonly ConcurrentDictionary<QualifiedName, ModelDescriptor> _entries = new();

    /// <summary>
    /// Registers <paramref name="descriptor"/> under its
    /// <see cref="ModelDescriptor.QualifiedName"/>. When <paramref name="replace"/>
    /// is <see langword="true"/>, returns the previous descriptor (if any)
    /// so the caller can dispose its sessions. Otherwise throws on conflict.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A model with the same qualified name is already registered and
    /// <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public ModelDescriptor? Register(ModelDescriptor descriptor, bool replace = false)
    {
        QualifiedName key = descriptor.QualifiedName;
        if (replace)
        {
            ModelDescriptor? previous = _entries.TryGetValue(key, out ModelDescriptor? p) ? p : null;
            _entries[key] = descriptor;
            return previous;
        }

        if (!_entries.TryAdd(key, descriptor))
        {
            throw new InvalidOperationException(
                $"Model '{key}' is already registered. " +
                "Use CREATE OR REPLACE MODEL to overwrite.");
        }
        return null;
    }

    /// <summary>
    /// Removes the model at <paramref name="name"/>. Returns the removed
    /// descriptor (with its bound sessions) so the caller can dispose,
    /// or <see langword="null"/> when no entry existed.
    /// </summary>
    public ModelDescriptor? Unregister(QualifiedName name)
        => _entries.TryRemove(name, out ModelDescriptor? removed) ? removed : null;

    /// <summary>Exact qualified lookup.</summary>
    public bool TryGet(QualifiedName name, [NotNullWhen(true)] out ModelDescriptor? descriptor)
        => _entries.TryGetValue(name, out descriptor);

    /// <summary>
    /// Search-path-aware lookup. An explicit <paramref name="explicitSchema"/>
    /// goes straight to that schema; an unqualified name walks
    /// <paramref name="searchPath"/> in order, first hit wins.
    /// </summary>
    public bool TryResolve(
        string? explicitSchema,
        string name,
        IReadOnlyList<string> searchPath,
        [NotNullWhen(true)] out ModelDescriptor? descriptor)
    {
        if (explicitSchema is not null)
        {
            return _entries.TryGetValue(new QualifiedName(explicitSchema, name), out descriptor);
        }

        foreach (string schema in searchPath)
        {
            if (_entries.TryGetValue(new QualifiedName(schema, name), out descriptor))
            {
                return true;
            }
        }
        descriptor = null;
        return false;
    }

    /// <summary>All registered descriptors. Order is not guaranteed.</summary>
    public IReadOnlyCollection<ModelDescriptor> Entries =>
        (IReadOnlyCollection<ModelDescriptor>)_entries.Values;
}
