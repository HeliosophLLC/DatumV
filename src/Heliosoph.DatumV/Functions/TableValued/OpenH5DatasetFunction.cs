using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_h5_dataset(path, dataset_path) → table</c>. Opens an HDF5
/// dataset at the given in-file path and yields its rows with a single
/// typed column. The output schema is the dataset's real schema — the
/// validator peeks the file at plan time so
/// <c>SELECT value FROM open_h5_dataset('foo.h5', '/labels') WHERE
/// value &gt; 0</c> type-checks against the actual element kind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output shape.</strong>
/// A 1-D dataset of length N yields N rows, one element per row, with a
/// single column named after the dataset's leaf (e.g. <c>/labels</c> →
/// column <c>labels</c>). A 2-D dataset of shape (R, C) yields R rows
/// with the column carrying the C-element row slice as a typed array.
/// Scalar datasets yield one row with one cell. v1 refuses 3-D and
/// higher-rank tensors — projection semantics there depend on the
/// recipe and the row-vs-array boundary needs more thought before we
/// pick one.
/// </para>
/// <para>
/// <strong>Plan-time schema peek.</strong>
/// Both arguments must be constants at plan time. The validator opens
/// the file, looks up the dataset path, and reads the dtype +
/// dimensions to produce a real <see cref="Schema"/>. Non-constant
/// arguments (e.g. a column reference in a JOIN) throw
/// <see cref="FunctionArgumentException"/>; recipes inline the path
/// or pass a bound parameter that's substituted to a literal.
/// </para>
/// <para>
/// <strong>Supported element kinds (v1):</strong> signed and unsigned
/// 8/16/32/64-bit integers, IEEE single / double floats, fixed-width
/// and variable-length strings, plus booleans. Compound dtypes
/// (HDF5 "struct" columns), opaque blobs, references, bit fields, and
/// enumerated types throw at validation time — they need follow-up
/// that interacts with the Value Type Registry.
/// </para>
/// </remarks>
public sealed class OpenH5DatasetFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_h5_dataset";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an HDF5 dataset and yields its rows with a single typed column: " +
        "open_h5_dataset(path, dataset_path). The column's element kind matches the dataset's " +
        "on-disk dtype, peeked at plan time so projections type-check. 1-D datasets yield one " +
        "row per element; 2-D yield one row per outer slice as a typed array.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("dataset_path", DataKindMatcher.Exact(DataKind.String)),
            ],
            FixedOutputSchema: null),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        ValidateArgumentShape(argumentKinds);

        if (constantArguments[0] is not DataValue pathValue || pathValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be a constant STRING. " +
                "Inline the file path or pass it via a bound $parameter.");
        }
        if (constantArguments[1] is not DataValue dsValue || dsValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (dataset_path) must be a constant STRING.");
        }

        string path = pathValue.AsString(constantStore);
        string datasetPath = dsValue.AsString(constantStore);

        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name, $"HDF5 file not found: '{path}'.");
        }

        using var file = H5File.OpenRead(path);
        if (!TryLookupDataset(file, datasetPath, out IH5Dataset? dataset, out string? error))
        {
            throw new FunctionArgumentException(Name, error);
        }

        Hdf5DatasetType type = Hdf5DatasetType.From(dataset.Type, dataset.Space);
        if (!type.IsSupported)
        {
            throw new FunctionArgumentException(Name,
                $"HDF5 dataset '{datasetPath}' has dtype class {type.UnderlyingClass} " +
                "which isn't supported in v1.");
        }
        if (type.CompoundLayout is not null && type.Dimensions.Count > 1)
        {
            throw new FunctionArgumentException(Name,
                $"HDF5 compound dataset '{datasetPath}' has rank {type.Dimensions.Count}; " +
                "v1 only supports scalar and 1-D compound datasets (catalog-row shape). " +
                "Higher-rank compound arrays are deferred until a real file demands them.");
        }
        return BuildSchema(datasetPath, type);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 2)
        {
            throw new ArgumentException("open_h5_dataset requires 2 arguments: (path, dataset_path).");
        }

        string path = arguments[0].AsString();
        string datasetPath = arguments[1].AsString();

        await foreach (RowBatch batch in StreamRowsAsync(path, datasetPath, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        string datasetPath,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        using var file = H5File.OpenRead(path);
        if (!TryLookupDataset(file, datasetPath, out IH5Dataset? dataset, out string? error))
        {
            throw new InvalidDataException(error);
        }

        Hdf5DatasetType type = Hdf5DatasetType.From(dataset.Type, dataset.Space);
        Schema outputSchema = BuildSchema(datasetPath, type);
        ColumnLookup outputLookup = new([outputSchema.Columns[0].Name]);

        // Compound (HDF5 struct) dtype path: read raw bytes, intern a
        // Struct TypeId in the per-query registry, then decode each row's
        // fields per the layout and emit one Struct cell per row. Caller
        // already gated rank > 1 in ValidateArguments.
        if (type.CompoundLayout is { } compound)
        {
            await foreach (RowBatch compoundBatch in StreamCompoundRowsAsync(
                dataset, outputSchema.Columns[0], compound, outputLookup, context, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return compoundBatch;
            }
            yield break;
        }

        System.Array flat = Hdf5DatasetReader.ReadFlat(dataset, type);

        RowBatch? batch = null;
        await Task.Yield();

        if (type.IsScalar || type.Dimensions.Count == 1)
        {
            int rowCount = flat.Length;
            for (int i = 0; i < rowCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(outputLookup);
                DataValue[] row = context.Pool.RentDataValues(1);
                row[0] = Hdf5DatasetReader.ScalarAt(flat, i, type.ElementKind, batch.Arena);
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }
        else if (type.Dimensions.Count == 2)
        {
            // 2-D: outer rows of 1-D inner-axis arrays.
            int outer = checked((int)type.Dimensions[0]);
            int inner = checked((int)type.Dimensions[1]);
            for (int i = 0; i < outer; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(outputLookup);
                DataValue[] row = context.Pool.RentDataValues(1);
                row[0] = Hdf5DatasetReader.SliceArray(flat, i * inner, inner, type.ElementKind, batch.Arena);
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }
        else
        {
            // 3-D and higher: outer rows of (R-1)-dim multi-dim cells.
            // The inner shape is the dataset's dims minus the outer axis; product
            // is the per-row element count.
            int outer = checked((int)type.Dimensions[0]);
            int[] innerShape = new int[type.Dimensions.Count - 1];
            int innerElementCount = 1;
            for (int i = 0; i < innerShape.Length; i++)
            {
                innerShape[i] = checked((int)type.Dimensions[i + 1]);
                innerElementCount = checked(innerElementCount * innerShape[i]);
            }
            for (int i = 0; i < outer; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= context.RentRowBatch(outputLookup);
                DataValue[] row = context.Pool.RentDataValues(1);
                row[0] = Hdf5DatasetReader.SliceMultiDim(
                    flat, i * innerElementCount, innerElementCount, innerShape, type.ElementKind, batch.Arena);
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Streams rows for a compound-dtype dataset: reads the raw byte
    /// block once, interns the struct TypeId in the per-query
    /// <see cref="TypeRegistry"/>, and emits one Struct
    /// <see cref="DataValue"/> per row. Scalar (rank-0) compound yields
    /// a single row; 1-D compound (catalog-row shape) yields N rows.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> StreamCompoundRowsAsync(
        IH5Dataset dataset,
        ColumnInfo column,
        Hdf5CompoundLayout layout,
        ColumnLookup outputLookup,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ushort typeId = (ushort)context.Types.InternStructFromColumnInfoFields(column.Fields!);

        byte[] raw = Hdf5DatasetReader.ReadCompoundRaw(dataset);
        int rowBytes = layout.RowByteSize;
        int rowCount = raw.Length / rowBytes;

        RowBatch? batch = null;
        await Task.Yield();

        for (int r = 0; r < rowCount; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= context.RentRowBatch(outputLookup);

            ReadOnlySpan<byte> rowSpan = raw.AsSpan(r * rowBytes, rowBytes);
            DataValue[] fields = new DataValue[layout.Fields.Count];
            for (int f = 0; f < fields.Length; f++)
            {
                fields[f] = Hdf5DatasetReader.DecodeCompoundField(rowSpan, layout.Fields[f], batch.Arena);
            }

            DataValue[] row = context.Pool.RentDataValues(1);
            row[0] = DataValue.FromStruct(fields, batch.Arena, typeId);
            batch.Add(row);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    private void ValidateArgumentShape(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new FunctionArgumentException(Name,
                "requires 2 arguments: open_h5_dataset(path, dataset_path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name, "argument 1 (path) must be STRING.");
        }
        if (argumentKinds[1] != DataKind.String)
        {
            throw new FunctionArgumentException(Name, "argument 2 (dataset_path) must be STRING.");
        }
    }

    private static Schema BuildSchema(string datasetPath, Hdf5DatasetType type)
    {
        string columnName = ExtractLeafName(datasetPath);
        int rank = type.Dimensions.Count;

        // Compound dtype path: emit a Struct column whose Fields are the
        // mapped member descriptors. The runtime path interns this struct
        // shape against the per-query TypeRegistry on first row.
        if (type.CompoundLayout is { } compound)
        {
            ColumnInfo[] structFields = new ColumnInfo[compound.Fields.Count];
            for (int i = 0; i < structFields.Length; i++)
            {
                Hdf5CompoundField f = compound.Fields[i];
                structFields[i] = new ColumnInfo(f.Name, f.Kind, nullable: false);
            }
            return new Schema([new ColumnInfo(columnName, nullable: false, structFields)]);
        }

        ColumnInfo column;
        if (rank <= 1)
        {
            // Scalar / 1-D dataset → scalar column. The 1-D case yields N rows,
            // each with one element.
            column = new ColumnInfo(columnName, type.ElementKind, nullable: false);
        }
        else if (rank == 2)
        {
            // 2-D → R rows of 1-D inner-axis array cells. No multi-dim shape on
            // the column; the array length equals Dimensions[1].
            column = new ColumnInfo(columnName, type.ElementKind, nullable: false) { IsArray = true };
        }
        else
        {
            // 3-D+ → R rows of (R-1)-dim multi-dim cells. The schema carries the
            // inner shape so downstream projection / coercion knows the per-row
            // tensor dimensions at plan time.
            int[] fixedShape = new int[rank - 1];
            for (int i = 0; i < fixedShape.Length; i++)
            {
                fixedShape[i] = checked((int)type.Dimensions[i + 1]);
            }
            column = new ColumnInfo(columnName, type.ElementKind, nullable: false)
            {
                IsArray = true,
                IsMultiDim = true,
                FixedShape = fixedShape,
            };
        }
        return new Schema([column]);
    }

    /// <summary>
    /// Returns the last slash-separated segment of a dataset path, used
    /// as the output column name. <c>/spectra/flux</c> → <c>flux</c>;
    /// <c>/data</c> → <c>data</c>; bare <c>/</c> falls back to "value".
    /// </summary>
    private static string ExtractLeafName(string datasetPath)
    {
        int slash = datasetPath.LastIndexOf('/');
        if (slash < 0) return datasetPath;
        string leaf = datasetPath[(slash + 1)..];
        return string.IsNullOrEmpty(leaf) ? "value" : leaf;
    }

    private static bool TryLookupDataset(
        IH5Group root,
        string datasetPath,
        [NotNullWhen(true)] out IH5Dataset? dataset,
        [NotNullWhen(false)] out string? error)
    {
        dataset = null;
        error = null;

        try
        {
            // PureHDF accepts both "/foo/bar" and "foo/bar"; strip the
            // leading slash to match the relative-path form.
            string normalised = datasetPath.StartsWith('/') ? datasetPath[1..] : datasetPath;
            if (!root.LinkExists(normalised))
            {
                error = $"HDF5 path '{datasetPath}' not found in the file.";
                return false;
            }
            object obj = root.Get(normalised);
            if (obj is not IH5Dataset ds)
            {
                error = $"HDF5 path '{datasetPath}' is a {obj.GetType().Name.ToLowerInvariant()}, not a dataset.";
                return false;
            }
            dataset = ds;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to open HDF5 path '{datasetPath}': {ex.Message}";
            return false;
        }
    }
}
