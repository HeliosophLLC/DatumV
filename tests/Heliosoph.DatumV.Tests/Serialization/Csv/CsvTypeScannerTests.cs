using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;

namespace Heliosoph.DatumV.Tests.Serialization.Csv;

/// <summary>
/// Covers <see cref="CsvTypeScanner"/> — per-column type inference with range
/// narrowing, leading-zero preservation, date-format detection, and decision
/// logging.
/// </summary>
public sealed class CsvTypeScannerTests : ServiceTestBase
{
    // ──────────────── Integer narrowing ────────────────

    [Fact]
    public async Task UInt8Range_NarrowsToUInt8()
    {
        const string csv = "n\n0\n17\n255\n42\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "n", DataKind.UInt8, SchemaInferenceReason.NarrowedByObservedRange);
        Assert.Equal(0L, scan.Decisions[0]!.Evidence!["observed_min"]);
        Assert.Equal(255L, scan.Decisions[0]!.Evidence!["observed_max"]);
    }

    [Fact]
    public async Task Int8Range_NarrowsToInt8()
    {
        const string csv = "n\n-128\n127\n-5\n0\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "n", DataKind.Int8, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task UInt16Range_NarrowsToUInt16()
    {
        const string csv = "n\n0\n65535\n12345\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "n", DataKind.UInt16, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task Int32Range_NarrowsToInt32()
    {
        string csv = $"n\n{int.MinValue}\n{int.MaxValue}\n0\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "n", DataKind.Int32, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task Int64Range_StaysInt64()
    {
        string csv = $"n\n{long.MinValue}\n{long.MaxValue}\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "n", DataKind.Int64, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task ZeroOneRange_ClassifiedAsBoolean()
    {
        const string csv = "flag\n0\n1\n0\n1\n1\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "flag", DataKind.Boolean, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── Float narrowing ────────────────

    [Fact]
    public async Task Float32RoundTripSafeValues_NarrowToFloat32()
    {
        // 1.5, 2.5, 0.25 all round-trip through single precision exactly.
        const string csv = "x\n1.5\n2.5\n0.25\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "x", DataKind.Float32, SchemaInferenceReason.FloatNarrowedToFloat32);
    }

    [Fact]
    public async Task HighPrecisionValues_StayFloat64()
    {
        // 0.1 is not exactly representable in float32; (float)(double)0.1 != 0.1.
        const string csv = "x\n0.1\n0.2\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "x", DataKind.Float64, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task LargeIntegerAmongFloats_ForcesFloat64()
    {
        // Value > 2^24 loses precision in float32 even though float-parseable.
        const string csv = "x\n1.5\n33554433\n";  // 2^25 + 1
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "x", DataKind.Float64, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── Leading-zero detection ────────────────

    [Fact]
    public async Task LeadingZeroCode_KeptAsString()
    {
        const string csv = "code\n02931\n03012\n00123\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "code", DataKind.String, SchemaInferenceReason.KeptAsStringLeadingZeros);
        Assert.Equal(SchemaInferenceSeverity.Notable, scan.Decisions[0]!.Severity);
        Assert.Equal("02931", scan.Decisions[0]!.Evidence!["example"]);
    }

    [Fact]
    public async Task SingleZero_DoesNotTriggerLeadingZero()
    {
        const string csv = "n\n0\n5\n10\n";
        CsvScanResult scan = await ScanAsync(csv);

        // "0" alone is not a leading-zero pattern; still narrowable to UInt8.
        AssertColumn(scan, "n", DataKind.UInt8, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task DecimalStartingWithZero_DoesNotTriggerLeadingZero()
    {
        // "0.5" starts with "0" but IsAllDigits is false — not a zero-padded code.
        const string csv = "x\n0.5\n1.5\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.NotEqual(SchemaInferenceReason.KeptAsStringLeadingZeros, scan.Decisions[0]!.Reason);
    }

    // ──────────────── Boolean text ────────────────

    [Fact]
    public async Task TrueFalseValues_ClassifiedAsBoolean()
    {
        const string csv = "active\ntrue\nfalse\nTRUE\nFalse\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "active", DataKind.Boolean, SchemaInferenceReason.NarrowedByObservedRange);
    }

    // ──────────────── UUID ────────────────

    [Fact]
    public async Task ThirtySixCharGuids_ClassifiedAsUuid()
    {
        const string csv =
            "id\n" +
            "12345678-1234-1234-1234-123456789abc\n" +
            "abcdef01-2345-6789-abcd-ef0123456789\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "id", DataKind.Uuid, SchemaInferenceReason.NarrowedByObservedRange);
    }

    [Fact]
    public async Task ShortAlphanumericStrings_DoNotClassifyAsUuid()
    {
        // Regression test: earlier version skipped UUID check for length != 36
        // instead of eliminating the candidate, causing false Uuid classification.
        const string csv = "code\nHY234567\nHX789012\nHZ345678\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.Equal(DataKind.String, scan.Kinds[0]);
        Assert.NotEqual(SchemaInferenceReason.NarrowedByObservedRange, scan.Decisions[0]!.Reason);
    }

    [Fact]
    public async Task ShortBooleanStrings_DoNotClassifyAsDateTime()
    {
        // Regression test: earlier version skipped DateTime check for length < 8
        // instead of eliminating the candidate, causing false DateTime classification
        // on columns full of "true"/"false".
        const string csv = "flag\ntrue\nfalse\ntrue\ntrue\nfalse\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.Equal(DataKind.Boolean, scan.Kinds[0]);
    }

    // ──────────────── Temporal ────────────────

    [Fact]
    public async Task IsoDateTime_ClassifiedAsDateTime()
    {
        const string csv = "t\n2024-01-15 10:30:00\n2024-02-20 14:45:00\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "t", DataKind.TimestampTz, SchemaInferenceReason.DateFormatMatched);
    }

    [Fact]
    public async Task IsoDate_ClassifiedAsDate()
    {
        const string csv = "d\n2024-01-15\n2024-02-20\n2024-03-01\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "d", DataKind.Date, SchemaInferenceReason.DateFormatMatched);
    }

    [Fact]
    public async Task UsAmPmDateTime_ClassifiedAsDateTime()
    {
        const string csv =
            "t\n" +
            "\"01/15/2024 10:30:00 AM\"\n" +
            "\"02/20/2024 02:45:00 PM\"\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "t", DataKind.TimestampTz, SchemaInferenceReason.DateFormatMatched);
    }

    // ──────────────── Mixed + all-null ────────────────

    [Fact]
    public async Task MixedTypes_KeptAsString()
    {
        const string csv = "x\n1\n2\nhello\n3\n";
        CsvScanResult scan = await ScanAsync(csv);

        AssertColumn(scan, "x", DataKind.String, SchemaInferenceReason.KeptAsStringMixedFormats);
        Assert.Equal(SchemaInferenceSeverity.Notable, scan.Decisions[0]!.Severity);
    }

    [Fact]
    public async Task AllNullColumn_Defaults_WithWarningSeverity()
    {
        const string csv = "a,b\n1,\n2,\n3,\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.Equal(DataKind.String, scan.Kinds[1]);
        Assert.Equal(SchemaInferenceReason.AllNull, scan.Decisions[1]!.Reason);
        Assert.Equal(SchemaInferenceSeverity.Warning, scan.Decisions[1]!.Severity);
        Assert.Equal(3, scan.NullCountsPerColumn[1]);
    }

    [Fact]
    public async Task NullCount_MatchesEmptyAndNullLiteralFields()
    {
        const string csv = "a\n1\n\n3\nNULL\nnull\n5\n";
        CsvScanResult scan = await ScanAsync(csv);

        // 3 nulls: empty row, "NULL", "null" (case-insensitive).
        Assert.Equal(3L, scan.NullCountsPerColumn[0]);
    }

    // ──────────────── Row count + structural ────────────────

    [Fact]
    public async Task RowCount_MatchesNonHeaderLines()
    {
        const string csv = "id,name\n1,a\n2,b\n3,c\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.Equal(3L, scan.RowCount);
        Assert.True(scan.HasHeader);
    }

    [Fact]
    public async Task ColumnNames_PreservedFromHeader()
    {
        const string csv = "first,second,third\n1,2.5,hello\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.Equal(["first", "second", "third"], scan.ColumnNames);
    }

    [Fact]
    public async Task WarmedTemporalCache_IsUsableByDeserializer()
    {
        const string csv = "t\n2024-01-15\n2024-02-20\n";
        CsvScanResult scan = await ScanAsync(csv);

        Assert.NotNull(scan.WarmedTemporalCache);
        // The cache is reusable: a subsequent parse via TryParseDate should still succeed.
        bool parsed = scan.WarmedTemporalCache.TryParseDate("2024-03-01", 0, out DateOnly d);
        Assert.True(parsed);
        Assert.Equal(new DateOnly(2024, 3, 1), d);
    }

    // ──────────────── Custom null token ────────────────

    [Fact]
    public async Task NullTokenDot_StillInfersFloatForFredStyleSeries()
    {
        // FRED's fredgraph.csv writes "." for missing observations. Without
        // a null_token, the dot eliminates every numeric candidate and the
        // value column lands as String. With null_token = ".", the dots are
        // counted as nulls and the column narrows correctly.
        const string csv = "DATE,VALUE\n2020-01-02,3257.85\n2020-01-03,.\n2020-01-06,3246.28\n";
        CsvScanResult scan = await ScanAsync(csv,
            options: new Dictionary<string, string> { ["null_token"] = "." });

        int idx = Array.IndexOf(scan.ColumnNames, "VALUE");
        Assert.True(idx >= 0);
        // VALUE narrows to a float kind (Float32 if round-trip-safe, else Float64).
        Assert.True(scan.Kinds[idx] is DataKind.Float32 or DataKind.Float64,
            $"expected float, got {scan.Kinds[idx]}");
        Assert.Equal(1L, scan.NullCountsPerColumn[idx]);
    }

    [Fact]
    public async Task NullTokenDot_OmittedFromScan_FallsBackToString()
    {
        // Sanity: without null_token, the same payload lands as String (the
        // regression this feature exists to fix).
        const string csv = "DATE,VALUE\n2020-01-02,3257.85\n2020-01-03,.\n2020-01-06,3246.28\n";
        CsvScanResult scan = await ScanAsync(csv);

        int idx = Array.IndexOf(scan.ColumnNames, "VALUE");
        Assert.True(idx >= 0);
        Assert.Equal(DataKind.String, scan.Kinds[idx]);
    }

    [Fact]
    public async Task NullTokenNA_TreatsRStyleExportMarkerAsNull()
    {
        // R's write.csv emits "NA" for missing values. Same pattern, different token.
        const string csv = "n\n1\nNA\n3\n";
        CsvScanResult scan = await ScanAsync(csv,
            options: new Dictionary<string, string> { ["null_token"] = "NA" });

        AssertColumn(scan, "n", DataKind.UInt8, SchemaInferenceReason.NarrowedByObservedRange);
        Assert.Equal(1L, scan.NullCountsPerColumn[0]);
    }

    // ──────────────── skip_lines + comment (EDGAR-shaped preambles) ────────────────

    [Fact]
    public async Task SkipLinesAndComment_LandsCikColumnAsInt32_OnEdgarShapedInput()
    {
        // Synthesised in the actual EDGAR master.idx shape: 5 description
        // lines + 4 blank lines (9 total to skip), the pipe-delimited
        // header on line 10, a dashes separator on line 11, then real
        // data. skip_lines=9 + comment='-' should land CIK as an
        // unsigned integer.
        const string csv =
            "Description:           Master Index\n" +
            "Last Data Received:    March 31, 2024\n" +
            "Comments:              webmaster@sec.gov\n" +
            "Anonymous FTP:         ftp://ftp.sec.gov/\n" +
            "Cloud HTTP:            https://www.sec.gov/\n" +
            "\n\n\n\n" +
            "CIK|Company Name|Form Type|Date Filed|Filename\n" +
            "--------------------------------------------------------------------------------\n" +
            "1000045|NICHOLAS FINANCIAL INC|10-Q|2024-02-13|edgar/data/1000045/x.txt\n" +
            "1000228|HENRY SCHEIN INC|10-K|2024-02-13|edgar/data/1000228/y.txt\n" +
            "1000275|ROYAL BANK OF CANADA|FWP|2024-02-13|edgar/data/1000275/z.txt\n";
        CsvScanResult scan = await ScanAsync(csv,
            options: new Dictionary<string, string>
            {
                ["skip_lines"] = "9",
                ["comment"] = "-",
            });

        Assert.Equal(5, scan.ColumnNames.Length);
        Assert.Equal("CIK", scan.ColumnNames[0]);
        Assert.Equal("Company Name", scan.ColumnNames[1]);
        Assert.Equal("Form Type", scan.ColumnNames[2]);
        Assert.Equal("Date Filed", scan.ColumnNames[3]);
        Assert.Equal("Filename", scan.ColumnNames[4]);
        // Without comment='-' the dashes line would corrupt the CIK column
        // (a single-field row that adds nulls to the other four columns
        // and a non-numeric dash to column 0). With it, CIK narrows to
        // an unsigned 32-bit integer — every CIK is non-negative and the
        // observed max fits in UInt32, so the scanner picks the tightest
        // fit. Asserts the kind family rather than the exact name so a
        // future change from UInt32 → UInt16 / etc. doesn't fail spuriously.
        Assert.True(scan.Kinds[0] is DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.Int32,
            $"expected an integer kind for CIK, got {scan.Kinds[0]}");
        Assert.Equal(DataKind.Date, scan.Kinds[3]);
    }

    [Fact]
    public async Task SkipLines_PreambleAlone_KeepsHeaderNamesAndDataRows()
    {
        const string csv =
            "# generated by some tool\n" +
            "# at 2024-01-01\n" +
            "id,score\n" +
            "1,0.5\n" +
            "2,0.9\n";
        CsvScanResult scan = await ScanAsync(csv,
            options: new Dictionary<string, string> { ["skip_lines"] = "2" });

        Assert.Equal(2, scan.ColumnNames.Length);
        Assert.Equal("id", scan.ColumnNames[0]);
        Assert.Equal("score", scan.ColumnNames[1]);
        Assert.Equal(2, scan.RowCount);
    }

    [Fact]
    public async Task Comment_DropsHashLinesInTheMiddleOfData()
    {
        const string csv =
            "id,name\n" +
            "1,alice\n" +
            "# end of batch 1\n" +
            "2,bob\n" +
            "# end of batch 2\n" +
            "3,carol\n";
        CsvScanResult scan = await ScanAsync(csv,
            options: new Dictionary<string, string> { ["comment"] = "#" });

        Assert.Equal(3, scan.RowCount);
        Assert.Equal("id", scan.ColumnNames[0]);
        Assert.Equal(DataKind.UInt8, scan.Kinds[0]); // narrows 1..3
    }

    // ──────────────── Helpers ────────────────

    private static async Task<CsvScanResult> ScanAsync(
        string csvContent,
        IReadOnlyDictionary<string, string>? options = null)
    {
        MemoryFileDescriptor source = new(csvContent, fileName: "test.csv", options: options);
        return await CsvTypeScanner.ScanAsync(source);
    }

    private static void AssertColumn(
        CsvScanResult scan, string name, DataKind expectedKind, SchemaInferenceReason expectedReason)
    {
        int index = Array.IndexOf(scan.ColumnNames, name);
        Assert.True(index >= 0, $"Column '{name}' not found in scan result.");
        Assert.Equal(expectedKind, scan.Kinds[index]);
        Assert.NotNull(scan.Decisions[index]);
        Assert.Equal(expectedReason, scan.Decisions[index]!.Reason);
    }
}
