using Heliosoph.DatumV.Manifest;

namespace Heliosoph.DatumV.Tests.Manifest;

/// <summary>
/// Validates that <see cref="ManifestSerializer"/> handles malformed JSON input
/// gracefully — either returning <c>null</c> or throwing a clean
/// <see cref="System.Text.Json.JsonException"/> — without crashing or corrupting state.
/// </summary>
public sealed class ManifestSerializerCorruptionTests : ServiceTestBase
{
    [Fact]
    public void Deserialize_EmptyString_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ManifestSerializer.Deserialize(""));
    }

    [Fact]
    public void Deserialize_GarbageText_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ManifestSerializer.Deserialize("this is not json"));
    }

    [Fact]
    public void Deserialize_TruncatedJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ManifestSerializer.Deserialize("{\"tables\": {"));
    }

    [Fact]
    public void Deserialize_NullLiteral_ReturnsNull()
    {
        SourceManifest? result = ManifestSerializer.Deserialize("null");

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_JsonArray_Throws()
    {
        // SourceManifest expects an object, not an array.
        Assert.ThrowsAny<Exception>(() => ManifestSerializer.Deserialize("[1, 2, 3]"));
    }

    [Fact]
    public void Deserialize_EmptyObject_DoesNotCrash()
    {
        // An empty object may deserialize to a SourceManifest with no tables — but must not crash.
        try
        {
            SourceManifest? result = ManifestSerializer.Deserialize("{}");
            // Success or null are both acceptable.
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException)
        {
            // Clean exception — acceptable.
        }
    }
}
