namespace DatumIngest.Manifest;

/// <summary>
/// Characterizes the dominant character repertoire of a string column, inferred by
/// sampling values. Used by <see cref="ColumnRoleClassifier"/> to identify synthetic
/// identifier strings (hex-encoded hashes, base-64 tokens, etc.) that would otherwise
/// be classified as generic categorical strings.
/// </summary>
public enum CharacterClass
{
    /// <summary>
    /// General-purpose strings with no dominant restricted character set.
    /// Includes natural language, mixed content, punctuated text, or any values
    /// that do not satisfy a stricter category.
    /// </summary>
    Mixed,

    /// <summary>
    /// All sampled values consist exclusively of hexadecimal characters
    /// (<c>[0-9a-fA-F]</c>). Typical of UUIDs without hyphens, MD5/SHA digests,
    /// and other hex-encoded identifiers.
    /// </summary>
    Hexadecimal,

    /// <summary>
    /// All sampled values consist exclusively of Base64 characters
    /// (<c>[A-Za-z0-9+/=]</c>). Typical of encoded binary payloads, tokens,
    /// and API keys.
    /// </summary>
    Base64,

    /// <summary>
    /// All sampled values consist exclusively of alphanumeric characters
    /// (<c>[A-Za-z0-9]</c>) but do not qualify as hexadecimal. Typical of
    /// short codes, SKUs, and encoded identifiers.
    /// </summary>
    Alphanumeric,
}
