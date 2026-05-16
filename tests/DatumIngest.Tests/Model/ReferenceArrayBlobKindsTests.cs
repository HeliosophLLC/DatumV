using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip tests for the blob-element typed-array pairs
/// (<c>FromAudioArray</c>/<c>AsAudioArray</c>, etc.) added to close the
/// cross-arena copy gap for non-Image blob kinds. The four kinds share the
/// same slot-block layout as <c>Array&lt;Image&gt;</c>; these tests verify
/// the kind discriminator round-trips and that per-element bytes survive the
/// write/read cycle uniformly across N=0, N=1, and N≥2 inline/arena paths.
/// </summary>
public sealed class ReferenceArrayBlobKindsTests : ServiceTestBase
{
    // ───────────────────────── Audio ─────────────────────────

    [Fact]
    public void Audio_Empty_RoundTrips()
    {
        Arena arena = CreateArena();
        DataValue value = DataValue.FromAudioArray([], arena);

        Assert.Equal(DataKind.Audio, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);
        Assert.Empty(value.AsAudioArray(arena));
    }

    [Fact]
    public void Audio_SingleElement_FitsInline()
    {
        Arena arena = CreateArena();
        byte[] clip = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00 };
        DataValue value = DataValue.FromAudioArray([clip], arena);

        Assert.Equal(DataKind.Audio, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsInline);

        byte[][] recovered = value.AsAudioArray(arena);
        Assert.Single(recovered);
        Assert.Equal(clip, recovered[0]);
    }

    [Fact]
    public void Audio_MultipleElements_UsesArenaSlotBlock()
    {
        Arena arena = CreateArena();
        byte[] clip0 = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00 };
        byte[] clip1 = new byte[] { 0x49, 0x44, 0x33, 0x04 };
        byte[] clip2 = new byte[] { 0x4F, 0x67, 0x67, 0x53 };

        DataValue value = DataValue.FromAudioArray([clip0, clip1, clip2], arena);

        Assert.True(value.IsArray);
        Assert.False(value.IsInline);
        Assert.True(value.IsArenaBacked);

        byte[][] recovered = value.AsAudioArray(arena);
        Assert.Equal(3, recovered.Length);
        Assert.Equal(clip0, recovered[0]);
        Assert.Equal(clip1, recovered[1]);
        Assert.Equal(clip2, recovered[2]);
    }

    // ───────────────────────── Video ─────────────────────────

    [Fact]
    public void Video_MultipleElements_RoundTrips()
    {
        Arena arena = CreateArena();
        byte[] frame0 = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 }; // mp4 header start
        byte[] frame1 = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 };                          // matroska header start

        DataValue value = DataValue.FromVideoArray([frame0, frame1], arena);
        byte[][] recovered = value.AsVideoArray(arena);

        Assert.Equal(DataKind.Video, value.Kind);
        Assert.Equal(2, recovered.Length);
        Assert.Equal(frame0, recovered[0]);
        Assert.Equal(frame1, recovered[1]);
    }

    // ───────────────────────── Json ─────────────────────────

    [Fact]
    public void Json_MultipleElements_RoundTrips()
    {
        Arena arena = CreateArena();
        byte[] doc0 = System.Text.Encoding.UTF8.GetBytes("""{"id":1}""");
        byte[] doc1 = System.Text.Encoding.UTF8.GetBytes("""{"id":2,"name":"alice"}""");
        byte[] doc2 = System.Text.Encoding.UTF8.GetBytes("""{"id":3}""");

        DataValue value = DataValue.FromJsonArray([doc0, doc1, doc2], arena);
        byte[][] recovered = value.AsJsonArray(arena);

        Assert.Equal(DataKind.Json, value.Kind);
        Assert.Equal(3, recovered.Length);
        Assert.Equal(doc0, recovered[0]);
        Assert.Equal(doc1, recovered[1]);
        Assert.Equal(doc2, recovered[2]);
    }

    // ───────────────────────── PointCloud ─────────────────────────

    [Fact]
    public void PointCloud_MultipleElements_RoundTrips()
    {
        Arena arena = CreateArena();
        // Fake PointCloud header bytes (40-byte header per the format spec) +
        // an arbitrary tail. The array storage tier doesn't parse the header,
        // so any bytes round-trip cleanly.
        byte[] cloud0 = new byte[48];
        cloud0[0] = 0xDA; cloud0[1] = 0xDA;  // sentinel
        byte[] cloud1 = new byte[56];
        cloud1[0] = 0xC0; cloud1[1] = 0xC0;

        DataValue value = DataValue.FromPointCloudArray([cloud0, cloud1], arena);
        byte[][] recovered = value.AsPointCloudArray(arena);

        Assert.Equal(DataKind.PointCloud, value.Kind);
        Assert.Equal(2, recovered.Length);
        Assert.Equal(cloud0, recovered[0]);
        Assert.Equal(cloud1, recovered[1]);
    }

    // ───────────────────────── Cross-arena round-trip ─────────────────────────

    [Fact]
    public void Audio_CrossArena_BytesSurviveSourceDisposal()
    {
        // The InsertExecutor cross-arena copy uses this exact pattern: read
        // bytes out of the source arena via As*Array (which returns managed
        // byte[][]), then write into the target arena via From*Array.
        Arena source = CreateArena();
        byte[] clip0 = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00 };
        byte[] clip1 = new byte[] { 0x49, 0x44, 0x33, 0x04 };
        DataValue inSource = DataValue.FromAudioArray([clip0, clip1], source);

        byte[][] managed = inSource.AsAudioArray(source);

        Arena target = CreateArena();
        DataValue inTarget = DataValue.FromAudioArray(managed, target);

        // Reading the target value through the target arena must succeed and
        // produce the same elements — no dangling references to `source`.
        byte[][] recovered = inTarget.AsAudioArray(target);
        Assert.Equal(2, recovered.Length);
        Assert.Equal(clip0, recovered[0]);
        Assert.Equal(clip1, recovered[1]);
    }

    // ───────────────────────── Wrong-kind guard ─────────────────────────

    [Fact]
    public void AsAudioArray_OnImageValue_Throws()
    {
        Arena arena = CreateArena();
        DataValue imageArray = DataValue.FromImageArray(
            [new byte[] { 1, 2, 3 }], arena);

        Assert.Throws<InvalidOperationException>(() => imageArray.AsAudioArray(arena));
    }
}
