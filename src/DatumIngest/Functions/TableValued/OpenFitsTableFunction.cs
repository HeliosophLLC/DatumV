using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Fits;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_fits_table(path, ext) → table</c>. Opens a FITS binary-table
/// HDU and yields its rows with one output column per <c>TTYPEn</c> /
/// <c>TFORMn</c> declared in the file. The output schema is the catalog's
/// real schema — typed, named columns — not an opaque
/// <c>Array&lt;DataValue&gt;</c> shape.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time schema peek.</strong> Both arguments must be
/// constants at plan time (literals in source, or <c>$parameter</c>
/// references that <see cref="ParameterBinder"/> has substituted). The
/// validator opens the file, walks to the target HDU, and reads the
/// <c>TTYPEn</c>/<c>TFORMn</c> cards to produce a real
/// <see cref="Schema"/> — so <c>SELECT TARGETID, RA, DEC FROM
/// open_fits_table(...)</c> type-checks against the actual catalog
/// columns. Calling with a non-constant argument (e.g. a column
/// reference) throws <see cref="FunctionArgumentException"/>; the
/// recipe writer must inline the path/ext.
/// </para>
/// <para>
/// <strong>Supported TFORM types (v1):</strong> L (logical), B (byte),
/// I (Int16), J (Int32), K (Int64), E (Float32), D (Float64),
/// A (fixed-width string), plus <c>repeat &gt; 1</c> array forms of the
/// numeric types. Variable-length array columns (TFORM P/Q), complex
/// (C/M), and bit arrays (X) throw <see cref="NotSupportedException"/>
/// — files that need them land via a follow-up that walks the BINTABLE
/// heap region.
/// </para>
/// <para>
/// <c>ext</c> can be a STRING (matched against <c>EXTNAME</c>
/// case-insensitively) or an INT64 (HDU index, where 0 is the primary
/// HDU). EXTNAME matching is the recommended form — recipes that key on
/// index break when a file's HDU layout shifts.
/// </para>
/// </remarks>
public sealed class OpenFitsTableFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_fits_table";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a FITS binary-table HDU and yields its rows with one column per TTYPE/TFORM: " +
        "open_fits_table(path, ext). 'ext' is either an EXTNAME (STRING) or an HDU index (INT64). " +
        "Both arguments must be constants at plan time so the validator can peek the file's " +
        "TFORMs and surface real typed columns.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        // String-EXTNAME form.
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("extname", DataKindMatcher.Exact(DataKind.String)),
            ],
            FixedOutputSchema: null),
        // Int64-HDU-index form.
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("hdu_index", DataKindMatcher.Exact(DataKind.Int64)),
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
                "Recipes that need a runtime-bound path should inline the file path " +
                "rather than passing it through a column reference.");
        }
        if (constantArguments[1] is not DataValue extValue)
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (ext) must be a constant — STRING (EXTNAME) or INT64 (HDU index).");
        }

        // String args carry their bytes in the resolver's per-call store.
        // The TVF only needs to read them while ValidateArguments is on the
        // stack; the file peek happens before we return.
        string path = pathValue.AsString(constantStore);
        ExtensionSelector selector = extValue.Kind switch
        {
            DataKind.String => ExtensionSelector.ByName(extValue.AsString(constantStore)),
            DataKind.Int64 => ExtensionSelector.ByIndex(extValue.AsInt64()),
            DataKind.Int32 => ExtensionSelector.ByIndex(extValue.AsInt32()),
            DataKind.Int8 => ExtensionSelector.ByIndex(extValue.AsInt8()),
            _ => throw new FunctionArgumentException(Name,
                $"argument 2 (ext) must be STRING or INT64; got {extValue.Kind}."),
        };

        if (!TryOpenAndLocate(path, selector, out FitsHduDescriptor? hdu, out Stream? stream, out string? error))
        {
            throw new FunctionArgumentException(Name, error);
        }
        try
        {
            IReadOnlyList<FitsBinTableColumn> columns = FitsBinTableReader.ParseColumns(hdu);
            return FitsBinTableReader.BuildSchema(columns);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 2)
        {
            throw new ArgumentException("open_fits_table requires 2 arguments: (path, ext).");
        }

        string path = arguments[0].AsString();
        ExtensionSelector selector = arguments[1].Kind switch
        {
            DataKind.String => ExtensionSelector.ByName(arguments[1].AsString()),
            DataKind.Int64 => ExtensionSelector.ByIndex(arguments[1].AsInt64()),
            DataKind.Int32 => ExtensionSelector.ByIndex(arguments[1].AsInt32()),
            DataKind.Int8 => ExtensionSelector.ByIndex(arguments[1].AsInt8()),
            _ => throw new ArgumentException(
                $"open_fits_table: argument 2 (ext) must be STRING or INT64; got {arguments[1].Kind}."),
        };

        using FileFormatDescriptor descriptor = new(path);
        await foreach (RowBatch batch in StreamRowsAsync(descriptor, selector, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        FileFormatDescriptor descriptor,
        ExtensionSelector selector,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;
        await using Stream stream = await descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        Stream walkable = stream;
        MemoryStream? buffered = null;
        if (!stream.CanSeek)
        {
            buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            walkable = buffered;
        }

        try
        {
            if (!TryLocate(walkable, selector, out FitsHduDescriptor? hdu, out string? error))
            {
                throw new InvalidDataException(error);
            }

            IReadOnlyList<FitsBinTableColumn> columns = FitsBinTableReader.ParseColumns(hdu);
            ColumnLookup outputLookup = new([.. columns.Select(c => c.Name)]);

            walkable.Position = hdu.DataOffset;
            await foreach (RowBatch batch in FitsBinTableReader.StreamRowsAsync(
                hdu, walkable, columns, outputLookup, context, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    private void ValidateArgumentShape(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new FunctionArgumentException(Name,
                "requires 2 arguments: open_fits_table(path, ext).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        if (argumentKinds[1] is not (DataKind.String or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64))
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (ext) must be STRING (EXTNAME) or an integer (HDU index).");
        }
    }

    /// <summary>
    /// Plan-time helper: opens the file with a FileStream (no descriptor
    /// since we don't need the gzip path here — the plan-time peek is
    /// best-effort against uncompressed files, and recipes that ship
    /// gzipped catalogs surface their schema at runtime through
    /// ExecuteAsync's gzip-aware path).
    /// </summary>
    private static bool TryOpenAndLocate(
        string path,
        ExtensionSelector selector,
        [NotNullWhen(true)] out FitsHduDescriptor? hdu,
        [NotNullWhen(true)] out Stream? stream,
        [NotNullWhen(false)] out string? error)
    {
        hdu = null;
        stream = null;
        error = null;

        if (!File.Exists(path))
        {
            error = $"FITS file not found: '{path}'.";
            return false;
        }

        FileStream fs = File.OpenRead(path);
        if (!TryLocate(fs, selector, out hdu, out error))
        {
            fs.Dispose();
            return false;
        }
        stream = fs;
        return true;
    }

    /// <summary>
    /// Walks <paramref name="stream"/>'s HDUs until <paramref name="selector"/>
    /// matches, or returns <c>false</c> with a populated error message.
    /// </summary>
    private static bool TryLocate(
        Stream stream,
        ExtensionSelector selector,
        [NotNullWhen(true)] out FitsHduDescriptor? hdu,
        [NotNullWhen(false)] out string? error)
    {
        hdu = null;
        error = null;
        int index = 0;
        while (FitsHduDescriptor.TryReadNext(
            stream, isPrimary: index == 0, out FitsHduDescriptor? current))
        {
            bool match = selector.Matches(index, current);
            if (match)
            {
                if (current.Kind != FitsHduKind.BinTable)
                {
                    error = $"open_fits_table: HDU {index} ('{current.ExtName ?? "<unnamed>"}') "
                          + $"is kind={current.Kind}, not BINTABLE.";
                    return false;
                }
                hdu = current;
                return true;
            }
            current.SkipData(stream);
            index++;
        }

        error = selector.Kind switch
        {
            ExtensionSelectorKind.Name =>
                $"open_fits_table: no HDU with EXTNAME='{selector.Name}' in the file.",
            _ =>
                $"open_fits_table: HDU index {selector.Index} is past the end of the file.",
        };
        return false;
    }

    private enum ExtensionSelectorKind { Name, Index }

    private readonly struct ExtensionSelector
    {
        public ExtensionSelectorKind Kind { get; }
        public string Name { get; }
        public long Index { get; }

        private ExtensionSelector(ExtensionSelectorKind kind, string name, long index)
        {
            Kind = kind;
            Name = name;
            Index = index;
        }

        public static ExtensionSelector ByName(string name) =>
            new(ExtensionSelectorKind.Name, name, default);

        public static ExtensionSelector ByIndex(long index) =>
            new(ExtensionSelectorKind.Index, "", index);

        public bool Matches(int hduIndex, FitsHduDescriptor hdu)
        {
            if (Kind == ExtensionSelectorKind.Index) return hduIndex == Index;
            return hdu.ExtName is not null
                && string.Equals(hdu.ExtName, Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
