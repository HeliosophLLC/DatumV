using System.Diagnostics.CodeAnalysis;

using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Shared helpers for the <c>assert_*</c> scalar function family. Provides
/// uniform message handling (caller-supplied vs auto-generated), failure-time
/// value rendering, and the canonical throw shape.
/// </summary>
internal static class AssertHelpers
{
    /// <summary>
    /// Returns the user-supplied failure message at <paramref name="index"/>
    /// when present and non-null; returns <see langword="null"/> when the slot
    /// is absent (optional argument omitted) or carries a SQL null. Callers
    /// fall back to an auto-generated message on <see langword="null"/>.
    /// </summary>
    internal static string? UserMessage(ReadOnlySpan<ValueRef> args, int index)
    {
        if (args.Length <= index) return null;
        ValueRef m = args[index];
        return m.IsNull ? null : m.AsString();
    }

    /// <summary>
    /// Renders a <see cref="ValueRef"/> as a short display string suitable
    /// for embedding in an auto-generated failure message. Strings are
    /// single-quoted; arrays show length only (rendering elements is
    /// unbounded); other kinds defer to <see cref="DataValue.ToDisplayString"/>.
    /// Materialised reference values (e.g. images) fall back to their type
    /// name.
    /// </summary>
    internal static string Display(ValueRef v)
    {
        if (v.IsNull) return "null";
        if (v.IsArray) return $"[array of {v.GetArrayLength()} elements]";
        if (v.Kind == DataKind.String) return $"'{v.AsString()}'";
        // Inline-carryable primitives format through DataValue. Materialised
        // non-string values land in the fallback branch (Kind tag only).
        return v.InlineDataValue.IsNull
            ? v.Kind.ToString()
            : v.InlineDataValue.ToDisplayString();
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> with
    /// <paramref name="userMessage"/> when supplied, otherwise with
    /// <paramref name="defaultMessage"/>. The <see cref="DoesNotReturnAttribute"/>
    /// lets call sites omit a trailing return statement.
    /// </summary>
    [DoesNotReturn]
    internal static void Throw(string? userMessage, string defaultMessage) =>
        throw new InvalidOperationException(userMessage ?? defaultMessage);
}
