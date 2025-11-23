using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class LazyDataValueTests : ServiceTestBase
{
    [Fact]
    public void ValueIsNotForcedOnConstruction()
    {
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            forceCount++;
            return DataValue.FromFloat32(1.0f);
        }, DataKind.Float32);

        Assert.Equal(0, forceCount);
    }

    [Fact]
    public void ValueIsForcedOnFirstAccess()
    {
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            forceCount++;
            return DataValue.FromFloat32(42.0f);
        }, DataKind.Float32);

        DataValue result = lazy.Value;

        Assert.Equal(1, forceCount);
        Assert.Equal(42.0f, result.AsFloat32());
    }

    [Fact]
    public void ValueIsCachedAfterFirstForce()
    {
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            forceCount++;
            return DataValue.FromFloat32(42.0f);
        }, DataKind.Float32);

        DataValue first = lazy.Value;
        DataValue second = lazy.Value;

        Assert.Equal(1, forceCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public void KindIsAvailableWithoutForcing()
    {
        LazyDataValue lazy = new(() => DataValue.FromFloat32(1.0f), DataKind.Float32);

        Assert.Equal(DataKind.Float32, lazy.Kind);
    }

    [Fact]
    public async Task IsThreadSafe()
    {
        int forceCount = 0;
        LazyDataValue lazy = new(() =>
        {
            Interlocked.Increment(ref forceCount);
            return DataValue.FromFloat32(99.0f);
        }, DataKind.Float32);

        // Force from multiple threads simultaneously
        Task[] tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() =>
            {
                DataValue result = lazy.Value;
                Assert.Equal(99.0f, result.AsFloat32());
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // The thunk should execute at most once
        Assert.Equal(1, forceCount);
    }

    [Fact]
    public void NestedLazyValuesChainCorrectly()
    {
        int innerForceCount = 0;
        int outerForceCount = 0;
        Arena arena = new();

        // Simulates load_image(file_bytes) in inner SELECT
        LazyDataValue innerLazy = new(() =>
        {
            innerForceCount++;
            return DataValue.FromByteArray([0xFF, 0xD8], arena);
        }, DataKind.UInt8);

        // Simulates resize(raw_image) in outer SELECT
        LazyDataValue outerLazy = new(() =>
        {
            outerForceCount++;
            DataValue inner = innerLazy.Value;
            byte[] bytes = inner.AsUInt8Array(arena);
            return DataValue.FromByteArray([.. bytes, 0x00], arena);
        }, DataKind.UInt8);

        // Neither should be forced yet
        Assert.Equal(0, innerForceCount);
        Assert.Equal(0, outerForceCount);

        // Forcing outer should chain to inner
        DataValue result = outerLazy.Value;
        Assert.Equal(1, innerForceCount);
        Assert.Equal(1, outerForceCount);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0x00 }, result.AsUInt8Array(arena));
    }

    [Fact]
    public void ThunkExceptionIsPropagated()
    {
        LazyDataValue lazy = new(
            () => throw new InvalidOperationException("data unavailable"),
            DataKind.Float32);

        Assert.Throws<InvalidOperationException>(() => lazy.Value);
    }
}
