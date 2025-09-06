namespace DatumIngest.Tests.DatumFile;

using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="DatumFileEditor"/> operations: appending row groups,
/// replacing column pages, and adding columns to existing <c>.datum</c> files.
/// </summary>
public sealed class DatumFileEditorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_editor_{Guid.NewGuid():N}");

    public DatumFileEditorTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    // ──────────────────── AppendRowGroups ────────────────────

    [Fact]
    public void AppendRowGroups_AddsNewRowGroup()
    {
        string path = Path.Combine(_tempDirectory, "append.datum");
        DatumFileSchema schema = CreateSchema(("id", DataKind.Int32, false), ("name", DataKind.String, true));

        WriteInitialFile(path, schema, rowGroupSize: 4, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1), DataValue.FromString("Alice")),
            MakeRow(schema, DataValue.FromInt32(2), DataValue.FromString("Bob")),
        ]);

        // Encode a new row group with two more rows.
        List<DataValue> newIds = [DataValue.FromInt32(3), DataValue.FromInt32(4)];
        List<DataValue> newNames = [DataValue.FromString("Charlie"), DataValue.FromString("Diana")];
        DatumEncodedPage[] pages = EncodePages(schema, [newIds, newNames]);

        RowGroupPayload payload = new(2, pages);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AppendRowGroups(stream, [payload]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(2, reader.RowGroupCount);
        Assert.Equal(4, reader.TotalRowCount);

        DataValue[][] group0 = reader.ReadColumns(0, [0, 1]);
        Assert.Equal(1, group0[0][0].AsInt32());
        Assert.Equal("Bob", group0[1][1].AsString());

        DataValue[][] group1 = reader.ReadColumns(1, [0, 1]);
        Assert.Equal(3, group1[0][0].AsInt32());
        Assert.Equal("Diana", group1[1][1].AsString());
    }

    [Fact]
    public void AppendRowGroups_MultiplePayloads()
    {
        string path = Path.Combine(_tempDirectory, "append_multi.datum");
        DatumFileSchema schema = CreateSchema(("value", DataKind.Float32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromFloat32(1.0f)),
        ]);

        List<DataValue> batch1 = [DataValue.FromFloat32(2.0f), DataValue.FromFloat32(3.0f)];
        List<DataValue> batch2 = [DataValue.FromFloat32(4.0f)];

        RowGroupPayload payload1 = new(2, EncodePages(schema, [batch1]));
        RowGroupPayload payload2 = new(1, EncodePages(schema, [batch2]));

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AppendRowGroups(stream, [payload1, payload2]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(3, reader.RowGroupCount);
        Assert.Equal(4, reader.TotalRowCount);

        DataValue[][] group2 = reader.ReadColumns(2, [0]);
        Assert.Equal(4.0f, group2[0][0].AsFloat32(), 0.0001f);
    }

    [Fact]
    public void AppendRowGroups_EmptyList_IsNoOp()
    {
        string path = Path.Combine(_tempDirectory, "append_noop.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(42)),
        ]);

        long originalLength = new FileInfo(path).Length;

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AppendRowGroups(stream, []);
        }

        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    [Fact]
    public void AppendRowGroups_ColumnCountMismatch_Throws()
    {
        string path = Path.Combine(_tempDirectory, "append_mismatch.datum");
        DatumFileSchema schema = CreateSchema(("a", DataKind.Int32, false), ("b", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1), DataValue.FromInt32(2)),
        ]);

        // Encode only one column instead of two.
        DatumFileSchema wrongSchema = CreateSchema(("a", DataKind.Int32, false));
        List<DataValue> values = [DataValue.FromInt32(3)];
        DatumEncodedPage[] pages = EncodePages(wrongSchema, [values]);
        RowGroupPayload payload = new(1, pages);

        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.Throws<ArgumentException>(() => DatumFileEditor.AppendRowGroups(stream, [payload]));
    }

    // ──────────────────── ReplaceColumns ────────────────────

    [Fact]
    public void ReplaceColumns_UpdatesSingleColumn()
    {
        string path = Path.Combine(_tempDirectory, "replace.datum");
        DatumFileSchema schema = CreateSchema(("id", DataKind.Int32, false), ("score", DataKind.Float32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1), DataValue.FromFloat32(10.0f)),
            MakeRow(schema, DataValue.FromInt32(2), DataValue.FromFloat32(20.0f)),
        ]);

        // Replace the "score" column (index 1) with new values.
        DatumColumnDescriptor scoreDescriptor = schema.Columns[1];
        List<DataValue> newScores = [DataValue.FromFloat32(99.0f), DataValue.FromFloat32(88.0f)];
        DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(scoreDescriptor);
        DatumEncodedPage replacementPage = encoder.Encode(newScores, scoreDescriptor);

        ColumnPageReplacement replacement = new(ColumnIndex: 1, RowGroupIndex: 0, replacementPage);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.ReplaceColumns(stream, [replacement]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(1, reader.RowGroupCount);
        Assert.Equal(2, reader.TotalRowCount);

        DataValue[][] columns = reader.ReadColumns(0, [0, 1]);

        // IDs unchanged.
        Assert.Equal(1, columns[0][0].AsInt32());
        Assert.Equal(2, columns[0][1].AsInt32());

        // Scores replaced.
        Assert.Equal(99.0f, columns[1][0].AsFloat32(), 0.0001f);
        Assert.Equal(88.0f, columns[1][1].AsFloat32(), 0.0001f);
    }

    [Fact]
    public void ReplaceColumns_MultipleColumnsInSingleCall()
    {
        string path = Path.Combine(_tempDirectory, "replace_multi.datum");
        DatumFileSchema schema = CreateSchema(
            ("a", DataKind.Int32, false),
            ("b", DataKind.Int32, false),
            ("c", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1), DataValue.FromInt32(2), DataValue.FromInt32(3)),
        ]);

        // Replace columns "a" (0) and "c" (2).
        DatumEncodedPage pageA = EncodeColumn(schema.Columns[0], [DataValue.FromInt32(10)]);
        DatumEncodedPage pageC = EncodeColumn(schema.Columns[2], [DataValue.FromInt32(30)]);

        ColumnPageReplacement replaceA = new(ColumnIndex: 0, RowGroupIndex: 0, pageA);
        ColumnPageReplacement replaceC = new(ColumnIndex: 2, RowGroupIndex: 0, pageC);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.ReplaceColumns(stream, [replaceA, replaceC]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        DataValue[][] columns = reader.ReadColumns(0, [0, 1, 2]);

        Assert.Equal(10, columns[0][0].AsInt32());
        Assert.Equal(2, columns[1][0].AsInt32()); // Unchanged.
        Assert.Equal(30, columns[2][0].AsInt32());
    }

    [Fact]
    public void ReplaceColumns_EmptyList_IsNoOp()
    {
        string path = Path.Combine(_tempDirectory, "replace_noop.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
        ]);

        long originalLength = new FileInfo(path).Length;

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.ReplaceColumns(stream, []);
        }

        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    [Fact]
    public void ReplaceColumns_InvalidColumnIndex_Throws()
    {
        string path = Path.Combine(_tempDirectory, "replace_bad_col.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
        ]);

        DatumEncodedPage page = EncodeColumn(schema.Columns[0], [DataValue.FromInt32(2)]);
        ColumnPageReplacement replacement = new(ColumnIndex: 5, RowGroupIndex: 0, page);

        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.Throws<ArgumentOutOfRangeException>(() => DatumFileEditor.ReplaceColumns(stream, [replacement]));
    }

    [Fact]
    public void ReplaceColumns_InvalidRowGroupIndex_Throws()
    {
        string path = Path.Combine(_tempDirectory, "replace_bad_rg.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
        ]);

        DatumEncodedPage page = EncodeColumn(schema.Columns[0], [DataValue.FromInt32(2)]);
        ColumnPageReplacement replacement = new(ColumnIndex: 0, RowGroupIndex: 3, page);

        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.Throws<ArgumentOutOfRangeException>(() => DatumFileEditor.ReplaceColumns(stream, [replacement]));
    }

    // ──────────────────── AddColumn ────────────────────

    [Fact]
    public void AddColumn_ExtendsSchemaWithNulls()
    {
        string path = Path.Combine(_tempDirectory, "addcol.datum");
        DatumFileSchema schema = CreateSchema(("id", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
            MakeRow(schema, DataValue.FromInt32(2)),
            MakeRow(schema, DataValue.FromInt32(3)),
        ]);

        // Add a nullable string column with all-null values.
        DatumColumnDescriptor newColumn = new("label", DataKind.String, DatumColumnFlags.Nullable);
        List<DataValue> nullValues = [
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.String),
        ];
        DatumEncodedPage nullPage = EncodeColumn(newColumn, nullValues);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AddColumn(stream, newColumn, [nullPage]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(2, reader.Schema.Columns.Count);
        Assert.Equal("id", reader.Schema.Columns[0].Name);
        Assert.Equal("label", reader.Schema.Columns[1].Name);
        Assert.Equal(3, reader.TotalRowCount);

        DataValue[][] columns = reader.ReadColumns(0, [0, 1]);
        Assert.Equal(1, columns[0][0].AsInt32());
        Assert.Equal(2, columns[0][1].AsInt32());
        Assert.Equal(3, columns[0][2].AsInt32());

        Assert.True(columns[1][0].IsNull);
        Assert.True(columns[1][1].IsNull);
        Assert.True(columns[1][2].IsNull);
    }

    [Fact]
    public void AddColumn_WithDefaultValue()
    {
        string path = Path.Combine(_tempDirectory, "addcol_default.datum");
        DatumFileSchema schema = CreateSchema(("id", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(10)),
            MakeRow(schema, DataValue.FromInt32(20)),
        ]);

        // Add an Int32 column with default value 0.
        DatumColumnDescriptor newColumn = new("count", DataKind.Int32);
        List<DataValue> defaultValues = [DataValue.FromInt32(0), DataValue.FromInt32(0)];
        DatumEncodedPage defaultPage = EncodeColumn(newColumn, defaultValues);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AddColumn(stream, newColumn, [defaultPage]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(2, reader.Schema.Columns.Count);
        Assert.Equal("count", reader.Schema.Columns[1].Name);

        DataValue[][] columns = reader.ReadColumns(0, [0, 1]);
        Assert.Equal(10, columns[0][0].AsInt32());
        Assert.Equal(0, columns[1][0].AsInt32());
        Assert.Equal(0, columns[1][1].AsInt32());
    }

    [Fact]
    public void AddColumn_MultipleRowGroups()
    {
        string path = Path.Combine(_tempDirectory, "addcol_multi_rg.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        // Write with a tiny row group size to force multiple row groups.
        WriteInitialFile(path, schema, rowGroupSize: 2, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
            MakeRow(schema, DataValue.FromInt32(2)),
            MakeRow(schema, DataValue.FromInt32(3)),
            MakeRow(schema, DataValue.FromInt32(4)),
            MakeRow(schema, DataValue.FromInt32(5)),
        ]);

        // Verify we got multiple row groups.
        int originalRowGroupCount;
        using (DatumFileReader check = DatumFileReader.Open(path))
        {
            originalRowGroupCount = check.RowGroupCount;
        }

        Assert.True(originalRowGroupCount >= 2, $"Expected >=2 row groups but got {originalRowGroupCount}.");

        // Add a nullable column with null pages for each row group.
        DatumColumnDescriptor newColumn = new("tag", DataKind.String, DatumColumnFlags.Nullable);

        // Read the existing state to know row counts per row group.
        DatumEncodedPage[] pages;
        using (FileStream readStream = File.OpenRead(path))
        {
            (DatumFileSchema _, DatumRowGroupDescriptor[] rowGroups, long _) =
                DatumFileReader.ReadFooterAndHeader(readStream);

            pages = new DatumEncodedPage[rowGroups.Length];
            DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(newColumn);

            for (int groupIndex = 0; groupIndex < rowGroups.Length; groupIndex++)
            {
                int rowCount = (int)rowGroups[groupIndex].RowCount;
                List<DataValue> nulls = new(rowCount);
                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    nulls.Add(DataValue.Null(DataKind.String));
                }

                pages[groupIndex] = encoder.Encode(nulls, newColumn);
            }
        }

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AddColumn(stream, newColumn, pages);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(originalRowGroupCount, reader.RowGroupCount);
        Assert.Equal(5, reader.TotalRowCount);
        Assert.Equal(2, reader.Schema.Columns.Count);
        Assert.Equal("tag", reader.Schema.Columns[1].Name);

        // Verify all rows in all row groups.
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            DataValue[][] columns = reader.ReadColumns(groupIndex, [0, 1]);

            for (int rowIndex = 0; rowIndex < columns[0].Length; rowIndex++)
            {
                Assert.True(columns[1][rowIndex].IsNull,
                    $"Expected null at row group {groupIndex}, row {rowIndex}.");
            }
        }
    }

    [Fact]
    public void AddColumn_PageCountMismatch_Throws()
    {
        string path = Path.Combine(_tempDirectory, "addcol_mismatch.datum");
        DatumFileSchema schema = CreateSchema(("x", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(1)),
        ]);

        DatumColumnDescriptor newColumn = new("y", DataKind.Int32);
        // Provide 0 pages when 1 is expected.
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.Throws<ArgumentException>(() => DatumFileEditor.AddColumn(stream, newColumn, []));
    }

    // ──────────────────── Combined operations ────────────────────

    [Fact]
    public void AppendThenReplace_RoundTrip()
    {
        string path = Path.Combine(_tempDirectory, "append_then_replace.datum");
        DatumFileSchema schema = CreateSchema(("value", DataKind.Int32, false));

        WriteInitialFile(path, schema, rowGroupSize: 8, rows:
        [
            MakeRow(schema, DataValue.FromInt32(100)),
        ]);

        // Append a second row group.
        List<DataValue> appendedValues = [DataValue.FromInt32(200)];
        RowGroupPayload appendPayload = new(1, EncodePages(schema, [appendedValues]));

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AppendRowGroups(stream, [appendPayload]);
        }

        // Replace column 0 in the first row group.
        DatumEncodedPage replacementPage = EncodeColumn(schema.Columns[0], [DataValue.FromInt32(999)]);
        ColumnPageReplacement replacement = new(ColumnIndex: 0, RowGroupIndex: 0, replacementPage);

        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.ReplaceColumns(stream, [replacement]);
        }

        using DatumFileReader reader = DatumFileReader.Open(path);
        Assert.Equal(2, reader.RowGroupCount);
        Assert.Equal(2, reader.TotalRowCount);

        DataValue[][] group0 = reader.ReadColumns(0, [0]);
        Assert.Equal(999, group0[0][0].AsInt32());

        DataValue[][] group1 = reader.ReadColumns(1, [0]);
        Assert.Equal(200, group1[0][0].AsInt32());
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Creates a <see cref="DatumFileSchema"/> from the given column specifications.
    /// </summary>
    private static DatumFileSchema CreateSchema(params (string Name, DataKind Kind, bool Nullable)[] columns)
    {
        List<DatumColumnDescriptor> descriptors = new(columns.Length);

        foreach ((string name, DataKind kind, bool nullable) in columns)
        {
            DatumColumnFlags flags = nullable ? DatumColumnFlags.Nullable : DatumColumnFlags.None;
            descriptors.Add(new DatumColumnDescriptor(name, kind, flags));
        }

        return new DatumFileSchema(descriptors);
    }

    /// <summary>
    /// Writes a <c>.datum</c> file using the low-level <see cref="DatumFileWriter"/>.
    /// </summary>
    private static void WriteInitialFile(
        string path,
        DatumFileSchema schema,
        int rowGroupSize,
        IReadOnlyList<Row> rows)
    {
        using DatumFileWriter writer = new(path);
        writer.SetRowGroupSize(rowGroupSize);
        writer.Initialize(schema);

        foreach (Row row in rows)
        {
            writer.WriteRow(row);
        }

        writer.Finalize();
    }

    /// <summary>
    /// Creates a <see cref="Row"/> from a schema and column values in schema order.
    /// </summary>
    private static Row MakeRow(DatumFileSchema schema, params DataValue[] values)
    {
        string[] names = new string[schema.ColumnCount];

        for (int index = 0; index < schema.ColumnCount; index++)
        {
            names[index] = schema.Columns[index].Name;
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Encodes column buffers into <see cref="DatumEncodedPage"/> instances,
    /// one per column in schema order.
    /// </summary>
    private static DatumEncodedPage[] EncodePages(
        DatumFileSchema schema,
        IReadOnlyList<List<DataValue>> columnBuffers)
    {
        DatumEncodedPage[] pages = new DatumEncodedPage[schema.ColumnCount];

        for (int columnIndex = 0; columnIndex < schema.ColumnCount; columnIndex++)
        {
            DatumColumnDescriptor descriptor = schema.Columns[columnIndex];
            DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
            pages[columnIndex] = encoder.Encode(columnBuffers[columnIndex], descriptor);
        }

        return pages;
    }

    /// <summary>
    /// Encodes a single column of values into a <see cref="DatumEncodedPage"/>.
    /// </summary>
    private static DatumEncodedPage EncodeColumn(
        DatumColumnDescriptor descriptor,
        IReadOnlyList<DataValue> values)
    {
        DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
        return encoder.Encode(values, descriptor);
    }
}
