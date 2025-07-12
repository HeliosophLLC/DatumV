namespace DatumIngest.LanguageServer;

using Superpower.Model;

/// <summary>
/// Intermediate token representation used during completion context analysis.
/// Captures the token kind, its text content, and its position in the source.
/// </summary>
internal sealed record TokenInfo(
    DatumIngest.Parsing.Tokens.SqlToken Kind,
    string Text,
    Position Position);
