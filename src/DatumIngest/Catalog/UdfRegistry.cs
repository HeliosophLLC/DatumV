using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// A registered user-defined scalar function (macro). The body is parsed once
/// at registration time and stored as an AST; the planner inlines it at every
/// call site by substituting parameter references with the call's argument
/// expressions.
/// </summary>
/// <param name="Name">
/// The unqualified UDF name (case-insensitive). SQL call sites use the
/// <c>udf.</c> prefix — <c>udf.foo(...)</c> — but the registry keys are
/// stored without the prefix.
/// </param>
/// <param name="Parameters">The declared parameters in order.</param>
/// <param name="ReturnTypeName">
/// Optional return-type annotation from <c>RETURNS TYPE</c>. v1 stores this
/// for introspection but does not type-check the body against it.
/// </param>
/// <param name="Body">
/// The parsed scalar expression evaluated at every call site, with parameter
/// references in scope. Substitution at inlining time replaces
/// <see cref="ColumnReference"/> nodes whose name matches a parameter with
/// the corresponding call-site argument expression.
/// </param>
public sealed record UdfDescriptor(
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string? ReturnTypeName,
    Expression Body);

/// <summary>
/// Process-scoped registry of user-defined scalar functions for a single
/// <see cref="TableCatalog"/>. Lookups are case-insensitive on the
/// unqualified UDF name. The planner consults this registry during
/// <c>Plan(...)</c> to inline every <c>udf.X(...)</c> call site.
/// </summary>
public sealed class UdfRegistry
{
    private readonly ConcurrentDictionary<string, UdfDescriptor> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="descriptor"/>. By default, throws if a UDF
    /// with the same name already exists. Pass <paramref name="replace"/> to
    /// overwrite — used by <c>CREATE OR REPLACE FUNCTION</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A UDF with the same name is already registered and <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public void Register(UdfDescriptor descriptor, bool replace = false)
    {
        if (replace)
        {
            _entries[descriptor.Name] = descriptor;
            return;
        }

        if (!_entries.TryAdd(descriptor.Name, descriptor))
        {
            throw new InvalidOperationException(
                $"UDF '{descriptor.Name}' is already registered. Use CREATE OR REPLACE FUNCTION to overwrite.");
        }
    }

    /// <summary>
    /// Removes the UDF named <paramref name="name"/>. Returns <see langword="true"/>
    /// when an entry was removed, <see langword="false"/> when no entry existed.
    /// </summary>
    public bool Unregister(string name) => _entries.TryRemove(name, out _);

    /// <summary>
    /// Looks up <paramref name="name"/>. Case-insensitive.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out UdfDescriptor? descriptor)
        => _entries.TryGetValue(name, out descriptor);

    /// <summary>
    /// Snapshot of all registered UDFs, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, UdfDescriptor> Entries => _entries;
}
