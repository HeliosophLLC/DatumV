using Heliosoph.DatumV.Manifest;

namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Signature-help payload returned to the editor when the cursor is inside a
/// function call's argument list. Field names align with the LSP
/// <c>SignatureHelp</c> specification so a future VS Code extension can map
/// 1:1.
/// </summary>
/// <remarks>
/// Per LSP, the editor uses <see cref="ActiveSignature"/> to select which
/// overload to display in the "N of M" carousel and
/// <see cref="ActiveParameter"/> to bold the current parameter in the
/// floating tooltip. Built-in functions with multiple call shapes (the
/// manifest's <see cref="FunctionSignature.AdditionalParameterShapes"/>)
/// surface every variant; the provider's default pick favours the shape
/// whose slot at the cursor matches the first token of the current
/// argument so the user doesn't have to cycle for the common case.
/// </remarks>
public sealed class SignatureHelp
{
    /// <summary>The signatures available at the cursor's call site. Always at least one entry.</summary>
    public required IReadOnlyList<SignatureInfo> Signatures { get; init; }

    /// <summary>Zero-based index into <see cref="Signatures"/>. <c>0</c> when no overload selection is needed.</summary>
    public int ActiveSignature { get; init; }

    /// <summary>
    /// Zero-based index of the parameter the cursor is currently filling in.
    /// Computed from comma count at the call's brace depth — clamps to the
    /// last parameter when the user has typed past the declared arity (so
    /// the popup keeps something sensible visible).
    /// </summary>
    public int ActiveParameter { get; init; }
}

/// <summary>
/// One signature variant: the full call shape the editor prints in the
/// tooltip, plus per-parameter sub-ranges that highlight the current
/// parameter.
/// </summary>
public sealed class SignatureInfo
{
    /// <summary>
    /// The full signature string — e.g. <c>"udf.RewriteCaption(@caption: STRING) → STRING"</c>.
    /// Renders as the header of the floating tooltip.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Optional documentation shown below the signature: model description,
    /// function category, etc. <see langword="null"/> when no doc text is
    /// available.
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>Per-parameter info matching the order of arguments in the signature.</summary>
    public required IReadOnlyList<ParameterInfo> Parameters { get; init; }
}

/// <summary>
/// One parameter within a signature. The label is the substring of the
/// enclosing <see cref="SignatureInfo.Label"/> that the editor highlights
/// when this parameter is active.
/// </summary>
public sealed class ParameterInfo
{
    /// <summary>The parameter's display substring (e.g. <c>"@caption: STRING"</c>).</summary>
    public required string Label { get; init; }

    /// <summary>Optional per-parameter documentation. Currently always <see langword="null"/>; reserved for future hover-over-parameter tooltips.</summary>
    public string? Documentation { get; init; }
}
