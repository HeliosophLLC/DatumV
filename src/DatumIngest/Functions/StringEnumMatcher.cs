using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Matcher specialisation for <see cref="DataKind.String"/> parameters
/// whose accepted values are drawn from an enumerated set. Exposed
/// concretely (not just as a base-class result) so the language server
/// can surface the value set as completion items when the cursor sits
/// inside the string literal in that parameter slot, without an
/// <c>is</c>-cast at every read site.
/// </summary>
/// <remarks>
/// <para>
/// Constructed via <see cref="DataKindMatcher.StringEnum"/>.
/// <see cref="Matches"/> behaves identically to a plain
/// <c>Exact(String)</c> matcher — the enum is an LS / documentation
/// hint, NOT a runtime contract. Implementing functions may accept
/// additional aliases that aren't in <see cref="Values"/>; the canonical
/// list lives here purely to drive editor suggestions.
/// </para>
/// <para>
/// Canonical use case: <c>blend(content, mode)</c>'s <c>mode</c>
/// parameter carries the full list of supported Porter-Duff /
/// photographer blend names. Future candidates include any function
/// whose string argument is drawn from a fixed vocabulary (font
/// families, file formats, sort orders, etc.).
/// </para>
/// </remarks>
public sealed class StringEnumMatcher : DataKindMatcher
{
    /// <summary>
    /// The enumerated set of accepted string values. Surfaced to the LS
    /// for completion; not enforced at plan time.
    /// </summary>
    public IReadOnlyList<string> Values { get; }

    internal StringEnumMatcher(IReadOnlyList<string> values)
    {
        Values = values;
    }

    /// <inheritdoc />
    public override bool Matches(DataKind kind) => kind == DataKind.String;

    /// <inheritdoc />
    public override string Describe()
    {
        // Cap the list at a sensible width — long enums (e.g. CSS colour
        // names) would otherwise dominate the parameter-shape display.
        if (Values.Count <= 6)
        {
            return "String (" + string.Join(" | ", Values.Select(v => $"'{v}'")) + ")";
        }
        return $"String (one of {Values.Count} values)";
    }
}
