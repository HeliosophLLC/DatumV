namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// A diagnostic message representing a parse error or warning in the SQL text.
/// Field names align with the LSP Diagnostic specification.
/// </summary>
public sealed class Diagnostic
{
    /// <summary>The human-readable error message.</summary>
    public required string Message { get; init; }

    /// <summary>The severity of the diagnostic.</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>The 0-based line number where the error starts.</summary>
    public required int StartLine { get; init; }

    /// <summary>The 0-based column number where the error starts.</summary>
    public required int StartColumn { get; init; }

    /// <summary>The 0-based line number where the error ends (inclusive).</summary>
    public required int EndLine { get; init; }

    /// <summary>The 0-based column number where the error ends (exclusive).</summary>
    public required int EndColumn { get; init; }
}

/// <summary>
/// Diagnostic severity levels, aligned with LSP DiagnosticSeverity values.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>A fatal parse error.</summary>
    Error = 1,

    /// <summary>A warning about potentially problematic SQL.</summary>
    Warning = 2,

    /// <summary>An informational hint.</summary>
    Information = 3,
}
