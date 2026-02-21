using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Registries;

/// <summary>
/// A registered user-defined scalar function. Two body shapes are supported:
/// <list type="bullet">
///   <item><description><b>Macro</b> — <see cref="ExpressionBody"/> is the parsed scalar
///   expression. The planner inlines it at every call site by substituting
///   parameter references with the call's argument expressions.</description></item>
///   <item><description><b>Procedural</b> — <see cref="StatementBody"/> is a
///   sequence of procedural statements (<c>DECLARE</c> / <c>RETURN</c> / control
///   flow) executed once per call against a fresh frame. Opaque to the planner
///   and the model-invocation hoister; <see cref="ReturnTypeName"/> is required
///   so call sites have a known scalar shape without analysing the body.</description></item>
/// </list>
/// Exactly one of <see cref="ExpressionBody"/> / <see cref="StatementBody"/> is non-null.
/// </summary>
/// <param name="SchemaName">
/// The schema this UDF lives in. Post-S7c every descriptor carries a real
/// schema membership — <c>CREATE FUNCTION myapp.foo()</c> lands at
/// <c>(myapp, foo)</c>; unqualified <c>CREATE FUNCTION foo()</c> lands at
/// the first DDL-capable schema on the session search_path (typically
/// <c>public</c>).
/// </param>
/// <param name="Name">
/// The unqualified UDF name (case-insensitive). Combine with
/// <see cref="SchemaName"/> via <see cref="QualifiedName"/> for the
/// canonical identity.
/// </param>
/// <param name="Parameters">
/// The declared parameters in order. Each parameter's <see cref="UdfParameter.IsNotNull"/>
/// flag controls whether the inliner wraps the substituted argument with a runtime
/// null assertion.
/// </param>
/// <param name="ReturnTypeName">
/// Return-type annotation from <c>RETURNS TYPE</c>. Optional for macro UDFs;
/// required for procedural UDFs because the planner can't infer a return shape
/// from an opaque statement body. When non-<see langword="null"/> on a macro,
/// the inliner wraps the substituted body with an implicit <c>CAST</c>; on a
/// procedural body the executor casts the value passed to <c>RETURN</c>.
/// </param>
/// <param name="ExpressionBody">
/// Macro form: the parsed scalar expression evaluated at every call site with
/// parameter references in scope. <see langword="null"/> for procedural UDFs.
/// </param>
/// <param name="ReturnIsNotNull">
/// When <see langword="true"/>, a runtime null assertion is applied to the
/// returned value.
/// </param>
/// <param name="StatementBody">
/// Procedural form: the body of <c>BEGIN…END</c> as a flat statement sequence.
/// <see langword="null"/> for macro UDFs.
/// </param>
/// <param name="IsPure">
/// Asserts referential transparency. Honoured by the planner's CSE / content-
/// addressed cache when those passes land.
/// </param>
/// <param name="SourceText">
/// The original <c>CREATE FUNCTION</c> SQL text, captured verbatim.
/// </param>
public sealed record UdfDescriptor(
    string SchemaName,
    string Name,
    IReadOnlyList<UdfParameter> Parameters,
    string? ReturnTypeName,
    Expression? ExpressionBody,
    bool ReturnIsNotNull = false,
    IReadOnlyList<Statement>? StatementBody = null,
    bool IsPure = false,
    string? SourceText = null)
{
    /// <summary>
    /// <see langword="true"/> when this descriptor is a procedural UDF
    /// (<see cref="StatementBody"/> is non-null), <see langword="false"/> for
    /// macro UDFs.
    /// </summary>
    public bool IsProcedural => StatementBody is not null;

    /// <summary>Canonical <c>(schema, name)</c> identity.</summary>
    public QualifiedName QualifiedName => new(SchemaName, Name);
}

/// <summary>
/// Process-scoped registry of user-defined scalar functions for a single
/// <see cref="TableCatalog"/>. Entries are keyed on
/// <see cref="QualifiedName"/> (case-insensitive). The planner consults this
/// registry during <c>Plan(...)</c> to inline every UDF call site —
/// unqualified calls walk the session search_path, qualified calls do an
/// exact lookup.
/// </summary>
public sealed class UdfRegistry
{
    private readonly ConcurrentDictionary<QualifiedName, UdfDescriptor> _entries = new();

    /// <summary>
    /// Registers <paramref name="descriptor"/> under its
    /// <see cref="UdfDescriptor.QualifiedName"/>. By default, throws if a UDF
    /// with the same qualified name already exists. Pass <paramref name="replace"/>
    /// to overwrite — used by <c>CREATE OR REPLACE FUNCTION</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A UDF with the same qualified name is already registered and
    /// <paramref name="replace"/> is <see langword="false"/>.
    /// </exception>
    public void Register(UdfDescriptor descriptor, bool replace = false)
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
                $"UDF '{key}' is already registered. Use CREATE OR REPLACE FUNCTION (or CREATE OR ALTER FUNCTION) to overwrite.");
        }
    }

    /// <summary>
    /// Removes the UDF at <paramref name="name"/>. Returns <see langword="true"/>
    /// when an entry was removed.
    /// </summary>
    public bool Unregister(QualifiedName name) => _entries.TryRemove(name, out _);

    /// <summary>Exact qualified lookup.</summary>
    public bool TryGet(QualifiedName name, [NotNullWhen(true)] out UdfDescriptor? descriptor)
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
        [NotNullWhen(true)] out UdfDescriptor? descriptor)
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
    public IReadOnlyCollection<UdfDescriptor> Entries => (IReadOnlyCollection<UdfDescriptor>)_entries.Values;

    private static readonly IReadOnlyList<string> DefaultSearchPath = new[] { "public", "system" };

    /// <summary>
    /// Back-compat bare-string lookup that walks the default
    /// <c>[public, system]</c> search path. Useful for tests and for
    /// the few call sites that don't yet pass an explicit search_path.
    /// </summary>
    public bool TryGet(string name, [NotNullWhen(true)] out UdfDescriptor? descriptor)
        => TryResolve(null, name, DefaultSearchPath, out descriptor);
}
