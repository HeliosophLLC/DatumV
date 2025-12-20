using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

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
/// <param name="Name">
/// The unqualified UDF name (case-insensitive). SQL call sites use the
/// <c>udf.</c> prefix — <c>udf.foo(...)</c> — but the registry keys are
/// stored without the prefix.
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
/// parameter references in scope. Substitution at inlining time replaces
/// <see cref="VariableExpression"/> nodes whose name matches a parameter with
/// the corresponding call-site argument expression. <see langword="null"/> for
/// procedural UDFs. Named to parallel <see cref="StatementBody"/> so the body-
/// shape duality is obvious at the call site.
/// </param>
/// <param name="ReturnIsNotNull">
/// When <see langword="true"/>, a runtime null assertion is applied to the
/// returned value (the inliner adds it for macros; the executor adds it for
/// procedural bodies).
/// </param>
/// <param name="StatementBody">
/// Procedural form: the body of <c>BEGIN…END</c> as a flat statement sequence.
/// Every control-flow path through the body must end with <c>RETURN expr</c>.
/// <see langword="null"/> for macro UDFs.
/// </param>
/// <param name="IsPure">
/// Asserts referential transparency (same arguments always produce the same
/// result, no observable side effects). Honoured by the planner's CSE / content-
/// addressed cache when those passes land; otherwise stored and surfaced via
/// <c>system_udfs</c>. Meaningful primarily for procedural UDFs — macros are
/// already inlined and CSE operates on the substituted expression.
/// </param>
/// <param name="SourceText">
/// The original <c>CREATE FUNCTION</c> SQL text, captured verbatim. Always
/// set for procedural UDFs (so the catalog can round-trip the body without
/// a Statement formatter); optional for macros (where the body expression
/// already round-trips through <c>QueryExplainer.FormatExpression</c>).
/// </param>
public sealed record UdfDescriptor(
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
    /// macro UDFs. Centralises the body-shape check so callers don't have to
    /// remember which slot is the discriminator.
    /// </summary>
    public bool IsProcedural => StatementBody is not null;
}

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
    /// overwrite — used by <c>CREATE OR REPLACE FUNCTION</c> and the T-SQL
    /// synonym <c>CREATE OR ALTER FUNCTION</c>.
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
                $"UDF '{descriptor.Name}' is already registered. Use CREATE OR REPLACE FUNCTION (or CREATE OR ALTER FUNCTION) to overwrite.");
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
