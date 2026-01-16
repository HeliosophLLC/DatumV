namespace DatumIngest.Manifest;

/// <summary>
/// On-disk schema version of <c>.datum-manifest</c> files. Surfaces incompatibility
/// when a file written by a newer binary lands in front of an older reader that
/// doesn't know the new <see cref="FeatureManifest"/> subtypes.
/// </summary>
/// <remarks>
/// Bumped each time a new <c>[JsonDerivedType]</c> registration adds a manifest
/// shape that older readers can't materialise. The reader rejects files whose
/// version exceeds <see cref="Current"/> with a clear "regenerate this manifest"
/// message; the alternative — silent failure on an unknown discriminator string
/// — surfaces as an opaque <see cref="System.Text.Json.JsonException"/> deep in
/// deserialization.
/// </remarks>
public static class ManifestSchemaVersion
{
    /// <summary>
    /// Schema version this binary writes and accepts. Files at this version or
    /// older are loadable; older files default to v1 (the implicit pre-versioning
    /// value) so manifests written by binaries that predate PR14d still round-trip.
    /// </summary>
    public const int Current = 1;
}
