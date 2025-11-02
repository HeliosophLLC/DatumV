using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Tests.DatumFile;

/// <summary>
/// Unit tests for <see cref="SidecarRegistry"/> — the per-catalog map from
/// <c>storeId</c> byte to <see cref="IBlobSource"/> introduced in Phase 9 so multi-
/// table queries with multiple sidecar-bound tables can disambiguate which sidecar
/// backs each DataValue.
/// </summary>
public sealed class SidecarRegistryTests : ServiceTestBase
{
    [Fact]
    public void Register_AssignsSequentialStoreIds()
    {
        SidecarRegistry registry = new();
        StubBlobSource a = new();
        StubBlobSource b = new();
        StubBlobSource c = new();

        Assert.Equal((byte)0, registry.Register(a));
        Assert.Equal((byte)1, registry.Register(b));
        Assert.Equal((byte)2, registry.Register(c));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void Resolve_ReturnsRegisteredSource()
    {
        SidecarRegistry registry = new();
        StubBlobSource a = new();
        StubBlobSource b = new();

        byte idA = registry.Register(a);
        byte idB = registry.Register(b);

        Assert.Same(a, registry.Resolve(idA));
        Assert.Same(b, registry.Resolve(idB));
    }

    [Fact]
    public void Resolve_UnregisteredSlot_ReturnsNull()
    {
        SidecarRegistry registry = new();
        registry.Register(new StubBlobSource());

        Assert.Null(registry.Resolve(5));
    }

    [Fact]
    public void Register_OverflowAt257ThrowsClearMessage()
    {
        SidecarRegistry registry = new();
        for (int i = 0; i < 256; i++)
        {
            registry.Register(new StubBlobSource());
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => registry.Register(new StubBlobSource()));
        Assert.Contains(">256", ex.Message);
        Assert.Contains("Split the query", ex.Message);
    }

    [Fact]
    public void Register_NullSource_Throws()
    {
        SidecarRegistry registry = new();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    /// <summary>Minimal IBlobSource for identity testing — never reads.</summary>
    private sealed class StubBlobSource : IBlobSource
    {
        public ReadOnlySpan<byte> Read(long offset, long length) => default;
        public void Dispose() { }
    }
}
