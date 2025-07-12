using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BloomFilter"/> — probabilistic membership filter
/// using double hashing (Kirsch–Mitzenmacker technique).
/// </summary>
public sealed class BloomFilterTests
{
    [Fact]
    public void Add_And_MayContain_KnownValues_ReturnsTrue()
    {
        BloomFilter filter = new(expectedElements: 100);
        DataValue value = DataValue.FromString("hello");
        filter.Add(value);

        Assert.True(filter.MayContain(value));
    }

    [Fact]
    public void MayContain_ValueNotAdded_ReturnsFalse()
    {
        BloomFilter filter = new(expectedElements: 100);
        filter.Add(DataValue.FromString("hello"));

        // A value that was never inserted should almost certainly report false.
        Assert.False(filter.MayContain(DataValue.FromString("goodbye")));
    }

    [Fact]
    public void MayContain_ManyDistinctValues_NoFalseNegatives()
    {
        BloomFilter filter = new(expectedElements: 1000, falsePositiveRate: 0.01);

        List<DataValue> inserted = new();
        for (int index = 0; index < 500; index++)
        {
            DataValue value = DataValue.FromScalar((float)index);
            filter.Add(value);
            inserted.Add(value);
        }

        // Every inserted value must be found — no false negatives allowed.
        foreach (DataValue value in inserted)
        {
            Assert.True(filter.MayContain(value), $"False negative for {value.AsScalar()}");
        }
    }

    [Fact]
    public void FalsePositiveRate_StaysBelowTarget()
    {
        int elementCount = 10_000;
        double targetRate = 0.05;
        BloomFilter filter = new(expectedElements: elementCount, falsePositiveRate: targetRate);

        for (int index = 0; index < elementCount; index++)
        {
            filter.Add(DataValue.FromScalar((float)index));
        }

        // Test against values that were NOT inserted.
        int falsePositives = 0;
        int testCount = 10_000;

        for (int index = elementCount; index < elementCount + testCount; index++)
        {
            if (filter.MayContain(DataValue.FromScalar((float)index)))
            {
                falsePositives++;
            }
        }

        double observedRate = (double)falsePositives / testCount;

        // Allow 2x the target rate to account for statistical variation.
        Assert.True(observedRate < targetRate * 2.0,
            $"False positive rate {observedRate:P2} exceeds 2x target {targetRate:P2}");
    }

    [Fact]
    public void Constructor_SetsOptimalSizing()
    {
        BloomFilter filter = new(expectedElements: 1000, falsePositiveRate: 0.01);

        // Optimal m ≈ -1000 * ln(0.01) / (ln(2))² ≈ 9585 bits.
        Assert.True(filter.BitCount >= 9000, $"BitCount {filter.BitCount} too small");
        Assert.True(filter.BitCount <= 11000, $"BitCount {filter.BitCount} too large");

        // Optimal k ≈ (m/n) * ln(2) ≈ 6.6 → 7.
        Assert.InRange(filter.HashCount, 5, 9);
    }

    [Fact]
    public void Constructor_ClampsSizingForTinyInput()
    {
        BloomFilter filter = new(expectedElements: 0);

        // Should clamp to minimum of 64 bits.
        Assert.True(filter.BitCount >= 64);
        Assert.True(filter.HashCount >= 1);
    }

    [Theory]
    [InlineData(DataKind.Scalar)]
    [InlineData(DataKind.UInt8)]
    [InlineData(DataKind.String)]
    [InlineData(DataKind.Date)]
    [InlineData(DataKind.DateTime)]
    public void Add_And_MayContain_AllScalarKinds(DataKind kind)
    {
        BloomFilter filter = new(expectedElements: 100);

        DataValue value = kind switch
        {
            DataKind.Scalar => DataValue.FromScalar(42.0f),
            DataKind.UInt8 => DataValue.FromUInt8(42),
            DataKind.String => DataValue.FromString("test"),
            DataKind.Date => DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataKind.DateTime => DataValue.FromDateTime(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
            _ => throw new ArgumentException($"Unsupported kind: {kind}")
        };

        filter.Add(value);
        Assert.True(filter.MayContain(value));
    }

    [Fact]
    public void Add_And_MayContain_VectorKind()
    {
        BloomFilter filter = new(expectedElements: 100);
        DataValue value = DataValue.FromVector([1.0f, 2.0f, 3.0f]);
        filter.Add(value);

        Assert.True(filter.MayContain(value));
    }

    [Fact]
    public void Add_And_MayContain_UInt8ArrayKind()
    {
        BloomFilter filter = new(expectedElements: 100);
        DataValue value = DataValue.FromUInt8Array([10, 20, 30]);
        filter.Add(value);

        Assert.True(filter.MayContain(value));
    }

    [Fact]
    public void InternalConstructor_FromBits_PreservesState()
    {
        BloomFilter original = new(expectedElements: 100, falsePositiveRate: 0.01);
        original.Add(DataValue.FromString("alpha"));
        original.Add(DataValue.FromString("beta"));

        // Reconstruct from raw bits (simulates deserialization).
        BloomFilter restored = new(original.Bits, original.BitCount, original.HashCount);

        Assert.True(restored.MayContain(DataValue.FromString("alpha")));
        Assert.True(restored.MayContain(DataValue.FromString("beta")));
        Assert.False(restored.MayContain(DataValue.FromString("gamma")));
    }

    [Fact]
    public void SizeInBytes_MatchesBitCountDivEight()
    {
        BloomFilter filter = new(expectedElements: 1000, falsePositiveRate: 0.01);

        int expectedBytes = (filter.BitCount + 7) / 8;
        Assert.Equal(expectedBytes, filter.SizeInBytes);
    }
}
