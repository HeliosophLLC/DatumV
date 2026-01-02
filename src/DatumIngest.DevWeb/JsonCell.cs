namespace DatumIngest.DevWeb;

internal sealed record JsonCell(
    string Kind,
    string? Text = null,
    string? Mime = null,
    string? DataB64 = null,
    // Populated for kind="media_array" — one entry per element. Single-blob
    // media still uses Mime + DataB64.
    IReadOnlyList<JsonMediaItem>? Items = null);

internal sealed record JsonMediaItem(string Mime, string DataB64);
