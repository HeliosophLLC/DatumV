using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Inference;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

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
/// </param>
/// <param name="StatementBody">
/// Procedural body. Always non-null on a valid model (CREATE MODEL parser
/// rejects expression bodies).
/// </param>
/// <param name="BoundSessions">
/// Map from session name to loaded inference session, populated by the
/// registrar after dispatcher load. Single-session bundles have one
/// entry keyed <c>"default"</c>.
/// </param>
/// <param name="ReturnIsNotNull">
/// When <see langword="true"/>, the procedural executor applies a runtime
/// null-assertion to the value passed to <c>RETURN</c>.
/// </param>
/// <param name="SourceText">
/// The original <c>CREATE MODEL</c> SQL text, captured verbatim for
/// persistence and round-trip display.
/// </param>
public sealed record ModelDescriptor(
    string SchemaName,
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string ReturnTypeName,
    string UsingPath,
    IReadOnlyList<Statement> StatementBody,
    IReadOnlyDictionary<string, IInferenceSession> BoundSessions,
    bool ReturnIsNotNull = false,
    string? SourceText = null)
{
    /// <summary>Canonical <c>(schema, name)</c> identity.</summary>
    public QualifiedName QualifiedName => new(SchemaName, Name);
}

/// <summary>
/// Process-scoped registry of SQL-defined models. Parallel to
/// <see cref="UdfRegistry"/> in shape — entries are keyed on
/// <see cref="QualifiedName"/>, lookup is search-path-aware — but
/// deliberately separate storage so <c>system.udfs</c> stays focused on
/// pure SQL routines and <c>system.models</c> surfaces both built-in
/// <see cref="DatumIngest.Models.ModelCatalog"/> entries and these
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
