using System.Text.Json;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Json;

namespace Heliosoph.DatumV.Tests.Serialization.Json;

/// <summary>
/// Covers <see cref="JsonTypeScanner"/> — per-column kind inference, integer
/// range narrowing, mixed-primitive and nested-value handling, union-of-keys
/// across rows, and null/absent parity.
/// </summary>
public sealed class JsonTypeScannerTests
{
    // ──────────────── Integer narrowing ────────────────

    [Fact]
    public void UInt8Range_NarrowsToUInt8()
    {
        JsonScanResult scan = Scan("""[{"n":0},{"n":17},{"n":255},{"n":42}]""");

        AssertColumn(scan, "n", DataKind.UInt8, SchemaInferenceReason.NarrowedByObservedRange);
        Assert.Equal(0L, scan.Decisions[0]!.Evidence!["observed_min"]);
        Assert.Equal(255L, scan.Decisions[0]!.Evidence!["observed_max"]);
    }

    [Fact]
    public void Int8Range_NarrowsToInt8()
    {
        JsonScanResult scan = Scan("""[{"n":-128},{"n":127},{"n":-5}]""");
        AssertColumn(scan, "n", DataKind.Int8, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public void Int32Range_NarrowsToInt32()
    {
        JsonScanResult scan = Scan($$"""[{"n":{{int.MinValue}}},{"n":{{int.MaxValue}}}]""");
        AssertColumn(scan, "n", DataKind.Int32, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public void Int64Range_StaysInt64()
    {
        JsonScanResult scan = Scan($$"""[{"n":{{long.MinValue}}},{"n":{{long.MaxValue}}}]""");
        AssertColumn(scan, "n", DataKind.Int64, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public void ZeroOneRange_ClassifiedAsBoolean()
    {
        JsonScanResult scan = Scan("""[{"flag":0},{"flag":1},{"flag":1}]""");
        AssertColumn(scan, "flag", DataKind.Boolean, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public void IntegerOverflowingInt64_PromotedToUInt128()
    {
        // 2^64 — overflows both Int64 and UInt64, fits Int128 and UInt128.
        JsonScanResult scan = Scan("""[{"id":18446744073709551616},{"id":42}]""");
        AssertColumn(scan, "id", DataKind.UInt128, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── Float narrowing ────────────────

    [Fact]
    public void Float32RoundTripSafeValues_NarrowToFloat32()
    {
        JsonScanResult scan = Scan("""[{"x":1.5},{"x":2.5},{"x":0.25}]""");
        AssertColumn(scan, "x", DataKind.Float32, SchemaInferenceReason.FloatNarrowedToFloat32);
    }

    [Fact]
    public void Float64RequiredValues_StayAsFloat64()
    {
        // 0.1 is not representable exactly in single precision.
        JsonScanResult scan = Scan("""[{"x":0.1},{"x":0.2}]""");
        AssertColumn(scan, "x", DataKind.Float64, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── Boolean / String ────────────────

    [Fact]
    public void AllBooleans_ClassifiedAsBoolean()
    {
        JsonScanResult scan = Scan("""[{"on":true},{"on":false},{"on":true}]""");
        AssertColumn(scan, "on", DataKind.Boolean, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public void AllStrings_ClassifiedAsString()
    {
        JsonScanResult scan = Scan("""[{"name":"alice"},{"name":"bob"}]""");
        AssertColumn(scan, "name", DataKind.String, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── Json fall-through ────────────────

    [Fact]
    public void NestedObject_ColumnIsJson()
    {
        JsonScanResult scan = Scan("""[{"meta":{"a":1}},{"meta":{"a":2}}]""");
        AssertColumn(scan, "meta", DataKind.Json, SchemaInferenceReason.KeptAsJson);
    }

    [Fact]
    public void NestedArray_ColumnIsJson()
    {
        JsonScanResult scan = Scan("""[{"bbox":[0,0,10,10]},{"bbox":[5,5,20,20]}]""");
        AssertColumn(scan, "bbox", DataKind.Json, SchemaInferenceReason.KeptAsJson);
    }

    [Fact]
    public void MixedPrimitiveFamilies_ColumnIsJson()
    {
        // First row String, second row Number → no primitive candidate survives.
        JsonScanResult scan = Scan("""[{"v":"hello"},{"v":42}]""");
        AssertColumn(scan, "v", DataKind.Json, SchemaInferenceReason.KeptAsJson);
    }

    [Fact]
    public void OnePrimitiveOneNested_ColumnIsJson()
    {
        JsonScanResult scan = Scan("""[{"v":42},{"v":{"nested":true}}]""");
        AssertColumn(scan, "v", DataKind.Json, SchemaInferenceReason.KeptAsJson);
    }

    // ──────────────── Null / absent / all-null ────────────────

    [Fact]
    public void AllNullColumn_DefaultsToStringWithWarning()
    {
        JsonScanResult scan = Scan("""[{"x":null},{"x":null}]""");
        AssertColumn(scan, "x", DataKind.String, SchemaInferenceReason.AllNull);
        Assert.Equal(SchemaInferenceSeverity.Warning, scan.Decisions[0]!.Severity);
    }

    [Fact]
    public void MissingKey_CountsAsNull_ButValuesStillInfer()
    {
        // n is present in rows 1, 3; absent in row 2. Result: kind from observed
        // values, NullCount reflects the absence.
        JsonScanResult scan = Scan("""[{"n":1,"name":"a"},{"name":"b"},{"n":2,"name":"c"}]""");

        int nIdx = Array.IndexOf(scan.ColumnNames, "n");
        Assert.True(nIdx >= 0);
        Assert.Equal(DataKind.UInt8, scan.Kinds[nIdx]);
        Assert.Equal(1L, scan.NullCountsPerColumn[nIdx]);
    }

    [Fact]
    public void ExplicitNull_AndAbsence_TreatedIdentically()
    {
        JsonScanResult scan = Scan("""[{"n":1},{"n":null},{}]""");

        int nIdx = Array.IndexOf(scan.ColumnNames, "n");
        Assert.Equal(2L, scan.NullCountsPerColumn[nIdx]);
    }

    // ──────────────── Union of keys ────────────────

    [Fact]
    public void KeysDiscoveredAcrossRows_ResultIsUnion()
    {
        JsonScanResult scan = Scan("""[{"a":1,"b":2},{"a":3,"c":4}]""");

        Assert.Contains("a", scan.ColumnNames);
        Assert.Contains("b", scan.ColumnNames);
        Assert.Contains("c", scan.ColumnNames);
    }

    [Fact]
    public void KeyDiscoveredLate_NullCountReflectsPriorRows()
    {
        // c first appears in row 3; rows 1 and 2 were missing it.
        JsonScanResult scan = Scan("""[{"a":1},{"a":2},{"a":3,"c":99}]""");

        int cIdx = Array.IndexOf(scan.ColumnNames, "c");
        Assert.Equal(2L, scan.NullCountsPerColumn[cIdx]);
    }

    // ──────────────── Root shapes ────────────────

    [Fact]
    public void RootSingleObject_ProducesOneRowTable()
    {
        JsonScanResult scan = Scan("""{"a":1,"b":"hi"}""");

        Assert.Equal(1L, scan.RowCount);
        Assert.Equal(2, scan.ColumnNames.Length);
        Assert.Contains("a", scan.ColumnNames);
        Assert.Contains("b", scan.ColumnNames);
    }

    // ──────────────── Helpers ────────────────

    private static JsonScanResult Scan(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonTypeScanner.Scan(EnumerateRows(document.RootElement));
    }

    private static IEnumerable<JsonElement> EnumerateRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("test fixture row not an object");
                yield return el;
            }
            yield break;
        }
        throw new InvalidOperationException($"test fixture root is {root.ValueKind}");
    }

    private static void AssertColumn(
        JsonScanResult scan, string name, DataKind expectedKind, SchemaInferenceReason expectedReason)
    {
        int index = Array.IndexOf(scan.ColumnNames, name);
        Assert.True(index >= 0, $"Column '{name}' not found in scan result.");
        Assert.Equal(expectedKind, scan.Kinds[index]);
        Assert.NotNull(scan.Decisions[index]);
        Assert.Equal(expectedReason, scan.Decisions[index]!.Reason);
    }
}
