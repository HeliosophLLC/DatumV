using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Matcher specialisation for <see cref="DataKind.Lambda"/> arguments that
/// carries the consumer's signature expectations (context + return-kind).
/// Exposed concretely (not just as a base-class result) so plan-time
/// validators and the language server can read the metadata without an
/// <c>is</c>-cast.
/// </summary>
/// <remarks>
/// <para>
/// Constructed via <see cref="DataKindMatcher.Lambda"/>. <see cref="Matches"/>
/// only verifies kind equality (the value is of kind
/// <see cref="DataKind.Lambda"/>); structural signature validation
/// (parameter count, the canonical parameter names from the context)
/// happens through <see cref="LambdaSignatureValidator"/> against the
/// supplied lambda's AST.
/// </para>
/// <para>
/// The language-server completion provider also reads
/// <see cref="ContextName"/> off this matcher: when the cursor is inside
/// the parameter slot of a function call whose parameter is a
/// <see cref="LambdaMatcher"/>, the completion scope switches to the
/// context's whitelist.
/// </para>
/// </remarks>
public sealed class LambdaMatcher : DataKindMatcher
{
    /// <summary>
    /// Context name expected by the consumer, or <see langword="null"/>
    /// for an unscoped lambda whose body inherits the surrounding scope's
    /// resolution rules.
    /// </summary>
    public string? ContextName { get; }

    /// <summary>Return-kind matcher applied to the lambda body's result.</summary>
    public DataKindMatcher Returns { get; }

    internal LambdaMatcher(string? contextName, DataKindMatcher returns)
    {
        ContextName = contextName;
        Returns = returns;
    }

    /// <inheritdoc />
    public override bool Matches(DataKind kind) => kind == DataKind.Lambda;

    /// <inheritdoc />
    public override string Describe()
    {
        string ctx = ContextName ?? "<unscoped>";
        return $"Lambda<{ctx}, returns: {Returns.Describe()}>";
    }
}
