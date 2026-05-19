using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Registries;

/// <summary>
/// A registered SQL view: a named substitution for a <see cref="SelectStatement"/>.
/// The planner expands every FROM reference to the view's qualified name with
/// the stored <see cref="Body"/> wrapped as a subquery source; views are pure
/// macros with no materialisation, no DML targeting, and no per-column
/// storage of their own.
/// </summary>
/// <param name="SchemaName">Schema this view lives in. Unqualified <c>CREATE VIEW</c> lands at the first DDL-capable schema on the session search_path (typically <c>public</c>).</param>
/// <param name="Name">Unqualified view name (case-insensitive).</param>
/// <param name="Body">The view's SELECT statement, captured at registration time.</param>
/// <param name="SourceText">The original <c>CREATE VIEW</c> SQL text, captured verbatim so persistence round-trips the user's formatting.</param>
public sealed record ViewDescriptor(
    string SchemaName,
    string Name,
    SelectStatement Body,
    string SourceText)
{
    /// <summary>Canonical <c>(schema, name)</c> identity.</summary>
    public QualifiedName QualifiedName => new(SchemaName, Name);
}

/// <summary>
/// Process-scoped registry of named views for a single <see cref="TableCatalog"/>.
/// Entries are keyed on <see cref="QualifiedName"/> (case-insensitive). The
/// source planner consults this registry whenever it walks a
/// <c>TableReference</c> — qualified references go straight to the matching
/// entry; unqualified references walk the session search_path.
/// </summary>
public sealed class ViewRegistry
{
    private readonly ConcurrentDictionary<QualifiedName, ViewDescriptor> _entries = new();

    /// <summary>
    /// Registers <paramref name="descriptor"/> under its
    /// <see cref="ViewDescriptor.QualifiedName"/>. By default, throws when a
    /// view at the same qualified name already exists; <paramref name="replace"/>
    /// overwrites — used by <c>CREATE OR REPLACE VIEW</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A view with the same qualified name is already registered and
    /// <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public void Register(ViewDescriptor descriptor, bool replace = false)
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
                $"View '{key}' is already registered. Use CREATE OR REPLACE VIEW to overwrite.");
        }
    }

    /// <summary>Removes the view at <paramref name="name"/>. Returns <see langword="true"/> when an entry was removed.</summary>
    public bool Unregister(QualifiedName name) => _entries.TryRemove(name, out _);

    /// <summary>Exact qualified lookup.</summary>
    public bool TryGet(QualifiedName name, [NotNullWhen(true)] out ViewDescriptor? descriptor)
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
        [NotNullWhen(true)] out ViewDescriptor? descriptor)
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
    public IReadOnlyCollection<ViewDescriptor> Entries => (IReadOnlyCollection<ViewDescriptor>)_entries.Values;
}
