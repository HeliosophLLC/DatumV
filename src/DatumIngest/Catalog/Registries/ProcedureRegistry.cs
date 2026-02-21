using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Registries;

/// <summary>
/// A registered procedural-batch macro. Unlike <see cref="UdfDescriptor"/>,
/// procedures are not inlined at plan time — the body is stored as a
/// <see cref="BlockStatement"/> AST plus the original source text, and
/// <c>CALL schema.name(...)</c> resolves the descriptor at runtime,
/// pushes a fresh batch context with the parameters auto-declared, and
/// runs the body.
/// </summary>
/// <param name="SchemaName">
/// The schema this procedure lives in. Post-S7c every descriptor carries
/// real schema membership.
/// </param>
/// <param name="Name">The unqualified procedure name (case-insensitive).</param>
/// <param name="Parameters">
/// The declared parameters in order. Each parameter's
/// <see cref="UdfParameter.IsNotNull"/> flag controls whether the
/// invocation wraps the supplied argument with a runtime null check.
/// </param>
/// <param name="Body">The procedural batch the procedure runs on invocation.</param>
/// <param name="SourceText">
/// The original <c>CREATE PROCEDURE</c> SQL text the descriptor was
/// registered from. Stored so persistence can write the user's exact
/// formatting back to the catalog file.
/// </param>
public sealed record ProcedureDescriptor(
    string SchemaName,
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    BlockStatement Body,
    string SourceText)
{
    /// <summary>Canonical <c>(schema, name)</c> identity.</summary>
    public QualifiedName QualifiedName => new(SchemaName, Name);
}

/// <summary>
/// Process-scoped registry of named procedures for a single
/// <see cref="TableCatalog"/>. Entries are keyed on
/// <see cref="QualifiedName"/> (case-insensitive). The procedural batch
/// executor consults this registry on every <c>CALL</c> call site —
/// unqualified calls walk search_path, qualified calls exact-match.
/// </summary>
public sealed class ProcedureRegistry
{
    private readonly ConcurrentDictionary<QualifiedName, ProcedureDescriptor> _entries = new();

    /// <summary>
    /// Registers <paramref name="descriptor"/> under its
    /// <see cref="ProcedureDescriptor.QualifiedName"/>. By default, throws
    /// if a procedure with the same qualified name already exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A procedure with the same qualified name is already registered and
    /// <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public void Register(ProcedureDescriptor descriptor, bool replace = false)
    {
        QualifiedName key = descriptor.QualifiedName;
        if (replace)
        {
            _entries[key] = descriptor;
            return;
        }

        if (!_entries.TryAdd(key, descriptor))
        {
            throw new InvalidOperationException(
                $"Procedure '{key}' is already registered. " +
                "Use CREATE OR REPLACE PROCEDURE (or CREATE OR ALTER PROCEDURE) to overwrite.");
        }
    }

    /// <summary>Removes the procedure at <paramref name="name"/>.</summary>
    public bool Unregister(QualifiedName name) => _entries.TryRemove(name, out _);

    /// <summary>Exact qualified lookup.</summary>
    public bool TryGet(QualifiedName name, [NotNullWhen(true)] out ProcedureDescriptor? descriptor)
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
        [NotNullWhen(true)] out ProcedureDescriptor? descriptor)
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
    public IReadOnlyCollection<ProcedureDescriptor> Entries => (IReadOnlyCollection<ProcedureDescriptor>)_entries.Values;

    private static readonly IReadOnlyList<string> DefaultSearchPath = new[] { "public", "system" };

    /// <summary>
    /// Back-compat bare-string lookup that walks the default
    /// <c>[public, system]</c> search path. Useful for tests.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out ProcedureDescriptor? descriptor)
        => TryResolve(null, name, DefaultSearchPath, out descriptor);
}
