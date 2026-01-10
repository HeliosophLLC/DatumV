using DatumIngest.Parsing.Ast;

namespace DatumIngest.Model;

/// <summary>
/// Live <c>IDENTITY</c> state surfaced by a provider — the captured
/// <see cref="IdentitySpec"/> together with the next counter value the
/// provider would hand out. Returned from <c>IAppendSession.IdentityState</c>
/// so an INSERT can reserve values without poking at provider internals.
/// </summary>
/// <param name="ColumnIndex">Index of the IDENTITY column in the schema.</param>
/// <param name="ColumnKind">The column's <see cref="DataKind"/>; reserved values are coerced into this.</param>
/// <param name="Spec">The <c>IDENTITY(seed, step)</c> spec.</param>
/// <param name="NextValue">The value the next reservation would return.</param>
public sealed record IdentityState(
    int ColumnIndex,
    DataKind ColumnKind,
    IdentitySpec Spec,
    long NextValue);
