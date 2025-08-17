using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="CompositeKey"/>, verifying value-equality semantics
/// and hash code consistency for compound join keys.
/// </summary>
public class CompositeKeyTests
{
    [Fact]
    public void Equals_IdenticalSinglePart_ReturnsTrue()
    {
        CompositeKey a = new([DataValue.FromFloat32(42f)]);
        CompositeKey b = new([DataValue.FromFloat32(42f)]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentSinglePart_ReturnsFalse()
    {
        CompositeKey a = new([DataValue.FromFloat32(1f)]);
        CompositeKey b = new([DataValue.FromFloat32(2f)]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_IdenticalMultipleParts_ReturnsTrue()
    {
        CompositeKey a = new([DataValue.FromFloat32(1f), DataValue.FromString("hello")]);
        CompositeKey b = new([DataValue.FromFloat32(1f), DataValue.FromString("hello")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentSecondPart_ReturnsFalse()
    {
        CompositeKey a = new([DataValue.FromFloat32(1f), DataValue.FromString("hello")]);
        CompositeKey b = new([DataValue.FromFloat32(1f), DataValue.FromString("world")]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentPartCount_ReturnsFalse()
    {
        CompositeKey a = new([DataValue.FromFloat32(1f)]);
        CompositeKey b = new([DataValue.FromFloat32(1f), DataValue.FromFloat32(2f)]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_StringKeys_ReturnsTrue()
    {
        CompositeKey a = new([DataValue.FromString("abc"), DataValue.FromString("def")]);
        CompositeKey b = new([DataValue.FromString("abc"), DataValue.FromString("def")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DictionaryLookup_Works()
    {
        Dictionary<CompositeKey, string> table = new();

        CompositeKey key1 = new([DataValue.FromFloat32(1f), DataValue.FromString("a")]);
        CompositeKey key2 = new([DataValue.FromFloat32(2f), DataValue.FromString("b")]);

        table[key1] = "first";
        table[key2] = "second";

        CompositeKey lookupKey = new([DataValue.FromFloat32(1f), DataValue.FromString("a")]);
        Assert.True(table.TryGetValue(lookupKey, out string? value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void OperatorEquals_Works()
    {
        CompositeKey a = new([DataValue.FromFloat32(5f)]);
        CompositeKey b = new([DataValue.FromFloat32(5f)]);

        Assert.True(a == b);
        Assert.False(a != b);
    }
}
