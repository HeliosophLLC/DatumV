using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// A registered procedural-batch macro. Unlike <see cref="UdfDescriptor"/>,
/// procedures are not inlined at plan time — the body is stored as a
/// <see cref="BlockStatement"/> AST plus the original source text, and
/// <c>EXEC proc.&lt;name&gt;(...)</c> resolves the descriptor at runtime,
/// pushes a fresh batch context with the parameters auto-declared, and
/// runs the body.
/// </summary>
/// <param name="Name">
/// The unqualified procedure name (case-insensitive). SQL call sites use
/// the <c>proc.</c> prefix — <c>proc.compute_cohort(...)</c> — but the
/// registry keys are stored without the prefix.
/// </param>
/// <param name="Parameters">
/// The declared parameters in order. Each parameter's
/// <see cref="UdfParameter.IsNotNull"/> flag controls whether the
/// invocation wraps the supplied argument with a runtime null check
/// before declaring the variable.
/// </param>
/// <param name="Body">
/// The procedural batch the procedure runs on invocation.
/// </param>
/// <param name="SourceText">
/// The original <c>CREATE PROCEDURE</c> SQL text the descriptor was
/// registered from. Stored so persistence can write the user's exact
/// formatting back to the catalog file and so introspection tables can
/// surface a faithful rendition.
/// </param>
public sealed record ProcedureDescriptor(
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    BlockStatement Body,
    string SourceText);

/// <summary>
/// Process-scoped registry of named procedures for a single
/// <see cref="TableCatalog"/>. Lookups are case-insensitive on the
/// unqualified procedure name. The procedural batch executor consults
/// this registry on every <c>EXEC proc.X(...)</c> call site to find the
/// descriptor whose body should run.
/// </summary>
public sealed class ProcedureRegistry
{
    private readonly ConcurrentDictionary<string, ProcedureDescriptor> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="descriptor"/>. By default, throws if a
    /// procedure with the same name already exists. Pass
    /// <paramref name="replace"/> to overwrite — used by
    /// <c>CREATE OR REPLACE PROCEDURE</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A procedure with the same name is already registered and
    /// <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public void Register(ProcedureDescriptor descriptor, bool replace = false)
    {
        if (replace)
        {
            _entries[descriptor.Name] = descriptor;
            return;
        }

        if (!_entries.TryAdd(descriptor.Name, descriptor))
        {
            throw new InvalidOperationException(
                $"Procedure '{descriptor.Name}' is already registered. " +
                "Use CREATE OR REPLACE PROCEDURE to overwrite.");
        }
    }

    /// <summary>
    /// Removes the procedure named <paramref name="name"/>. Returns
    /// <see langword="true"/> when an entry was removed,
    /// <see langword="false"/> when no entry existed.
    /// </summary>
    public bool Unregister(string name) => _entries.TryRemove(name, out _);

    /// <summary>
    /// Looks up <paramref name="name"/>. Case-insensitive.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out ProcedureDescriptor? descriptor)
        => _entries.TryGetValue(name, out descriptor);

    /// <summary>
    /// Snapshot of all registered procedures, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, ProcedureDescriptor> Entries => _entries;
}
