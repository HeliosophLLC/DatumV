namespace DatumIngest.DevWeb;

internal sealed record JsonCell(
    string Kind,
    string? Text = null,
    string? Mime = null,
    string? DataB64 = null);
