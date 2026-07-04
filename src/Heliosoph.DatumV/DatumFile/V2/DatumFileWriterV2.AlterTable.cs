using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2;

public sealed partial class DatumFileWriterV2
{
    /// <summary>
    /// Marks a column as soft-dropped. The column's data stays on
    /// disk (page directory, zone maps, sidecar references all
    /// preserved) but readers skip it at schema enumeration. Idempotent
    /// — calling twice on the same name is a no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only valid in append mode (the writer was opened via
    /// <see cref="OpenForAppend"/>). Initial-write callers that don't
    /// want the column should simply not include it in the schema
    /// passed to <see cref="Initialize(IReadOnlyList{ColumnDescriptorV2})"/>.
    /// </para>
    /// <para>
    /// Must be called before <see cref="FinalizeWriter"/>. Calls
    /// after finalize throw <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    /// <param name="columnName">
    /// Name of the column to drop. Match is case-sensitive and exact;
    /// throws <see cref="ArgumentException"/> if the name doesn't
    /// resolve to a column in the file's footer.
    /// </param>
    public void MarkColumnTombstoned(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "MarkColumnTombstoned requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");

        // Tombstone the LIVE column with this name. After a previous
        // DROP+ADD cycle the footer may have multiple entries sharing a
        // name (one tombstoned, one live); walk past the tombstoned
        // entries to the live one. Skipping tombstoned entries also
        // keeps `MarkColumnTombstoned("hash")` idempotent against an
        // already-dropped column — there's no live `hash` to tombstone,
        // so we throw "not found" with the same message a stranger
        // column would produce.
        for (int i = 0; i < _columns!.Length; i++)
        {
            if (_columns[i].Name == columnName && !_columns[i].IsTombstoned)
            {
                _columns[i] = _columns[i] with { IsTombstoned = true };
                return;
            }
        }

        throw new ArgumentException(
            $"Column '{columnName}' not found in the file's schema (no live column carries this " +
            "name; previously-dropped columns with the same name are tombstoned and not eligible). " +
            "Existing live columns: " +
            string.Join(", ", _columns.Where(c => !c.IsTombstoned).Select(c => c.Name)),
            nameof(columnName));
    }

    /// <summary>
    /// Adds a new column to the schema with all-null backfill for
    /// every existing row. After this call, the writer's effective
    /// column count is <c>previous + 1</c>; subsequent
    /// <see cref="WriteRowBatch"/> calls must supply values for the
    /// new column too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only valid in append mode (writer opened via
    /// <see cref="OpenForAppend"/>) and only callable while the
    /// writer's encoders have no pending unflushed rows for the
    /// existing columns — i.e., <see cref="AddColumn(ColumnDescriptorV2)"/> must come
    /// before any <see cref="WriteRowBatch"/> call in the session, OR
    /// after a <see cref="WriteRowBatch"/> whose row count made every
    /// column's encoder flush at the same boundary. The simplest rule:
    /// call <c>AddColumn</c> immediately after
    /// <see cref="OpenForAppend"/> and before any
    /// <c>WriteRowBatch</c>.
    /// </para>
    /// <para>
    /// PR6 requires the new column to be nullable
    /// (<see cref="ColumnDescriptorV2.IsNullable"/> = <c>true</c>) —
    /// the backfill is all-null and a non-nullable column with no
    /// values is undefined. Computed-default backfill is a future
    /// enhancement.
    /// </para>
    /// <para>
    /// The new column's pages stream out past EOF as the encoder
    /// fills, exactly mirroring the existing append-pages flow.
    /// They're column-major (contiguous) for the new column, while
    /// older columns' pages remain interleaved by their original
    /// flush order — invisible to readers because page directories
    /// record absolute offsets.
    /// </para>
    /// </remarks>
    public void AddColumn(ColumnDescriptorV2 column) =>
        AddColumn(column, defaultSqlFragment: null, computedSqlFragment: null, identityForNewColumn: null);

    /// <summary>
    /// As <see cref="AddColumn(ColumnDescriptorV2)"/>, plus an optional
    /// <c>DEFAULT</c> SQL-fragment that lands in the prologue's defaults
    /// table at finalize time. Future INSERTs that omit this column will
    /// pick up the default; pre-existing rows still backfill as NULL.
    /// </summary>
    public void AddColumn(ColumnDescriptorV2 column, string? defaultSqlFragment) =>
        AddColumn(column, defaultSqlFragment, computedSqlFragment: null, identityForNewColumn: null);

    /// <summary>
    /// As <see cref="AddColumn(ColumnDescriptorV2, string?)"/>, plus an
    /// optional <c>GENERATED ALWAYS AS</c> SQL-fragment persisted in the
    /// footer's computed-columns block. Mutually exclusive in practice:
    /// the catalog rejects DEFAULT + computed on the same column at
    /// validation time.
    /// </summary>
    public void AddColumn(ColumnDescriptorV2 column, string? defaultSqlFragment, string? computedSqlFragment) =>
        AddColumn(column, defaultSqlFragment, computedSqlFragment, identityForNewColumn: null);

    /// <summary>
    /// Full-fidelity <c>AddColumn</c> that additionally accepts an
    /// <see cref="IdentityWriterSpec"/> when the new column is declared
    /// <c>GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY</c>. The writer
    /// pumps sequential counter values into existing rows (instead of
    /// NULLs), sets up the prologue's identity state, and advances the
    /// running counter past the backfill. The catalog has already
    /// validated single-IDENTITY-per-table at this point — the writer
    /// asserts the invariant defensively.
    /// </summary>
    public void AddColumn(
        ColumnDescriptorV2 column,
        string? defaultSqlFragment,
        string? computedSqlFragment,
        IdentityWriterSpec? identityForNewColumn)
    {
        ArgumentNullException.ThrowIfNull(column);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "AddColumn requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");
        if (!_isAppend) throw new InvalidOperationException(
            "AddColumn is only supported in append mode (OpenForAppend). " +
            "For initial writes, include the column in Initialize's schema list.");

        if (!column.IsNullable)
        {
            throw new ArgumentException(
                $"AddColumn requires a nullable column; '{column.Name}' is non-nullable. " +
                "All-null backfill needs IsNullable = true.",
                nameof(column));
        }
        if (column.IsTombstoned)
        {
            throw new ArgumentException(
                "AddColumn cannot accept a column with IsTombstoned = true; " +
                "tombstone state is set via MarkColumnTombstoned, not AddColumn.",
                nameof(column));
        }

        // Reject collisions against LIVE columns only. Tombstoned
        // columns keep their footer entries (so compaction can reclaim
        // the data block later), but the user-facing name is freed —
        // matches PostgreSQL's behaviour where a dropped column's name
        // is immediately reusable. Two columns can coexist with the
        // same name as long as one is tombstoned; schema readers filter
        // tombstones, so live-column lookups stay unambiguous.
        foreach (ColumnDescriptorV2 existing in _columns!)
        {
            if (existing.Name == column.Name && !existing.IsTombstoned)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' already exists in the file's schema. " +
                    "DROP the existing column first if you need to re-add under the same name.",
                    nameof(column));
            }
        }

        // Sanity: every existing column's encoder must hold the same
        // pending row count. This is the lockstep invariant the writer
        // maintains across rehydration and WriteRowBatch (all columns
        // advance row-by-row in step). A divergence here would mean
        // some prior op corrupted writer state — fail loudly. The
        // backfill below pumps _totalRowsWritten nulls into the new
        // column, which aligns it to whatever lockstep state the
        // existing columns share regardless of value.
        if (_encoders!.Length > 0)
        {
            int referenceRowCount = _encoders[0].RowCount;
            for (int i = 1; i < _encoders.Length; i++)
            {
                if (_encoders[i].RowCount != referenceRowCount)
                {
                    throw new InvalidOperationException(
                        $"Writer state inconsistent at AddColumn: column '{_columns[0].Name}' " +
                        $"has {referenceRowCount} pending rows, column '{_columns[i].Name}' has " +
                        $"{_encoders[i].RowCount}. Existing columns must be in lockstep before " +
                        "adding a new column.");
                }
            }
        }

        // Grow per-column writer state arrays to accommodate the new
        // column at index N (where N was the previous count).
        int newIndex = _columns.Length;
        Array.Resize(ref _columns, newIndex + 1);
        Array.Resize(ref _encoders, newIndex + 1);
        Array.Resize(ref _pageDirectory, newIndex + 1);
        Array.Resize(ref _hierarchies, newIndex + 1);

        _columns[newIndex] = column;
        _encoders[newIndex] = PageEncoderFactoryV2.Create(column, _pageSize);
        _pageDirectory[newIndex] = new List<PageDescriptorV2>();
        _hierarchies[newIndex] = new ZoneMapHierarchyBuilderV2();

        // Stash the DEFAULT (if any) onto the prologue's defaults table.
        // Future INSERTs that omit this column will reload it from the file
        // and auto-fill via the existing CREATE TABLE default machinery.
        if (defaultSqlFragment is not null)
        {
            _columnDefaults ??= new List<ColumnDefaultV4>();
            _columnDefaults.Add(new ColumnDefaultV4(checked((ushort)newIndex), defaultSqlFragment));
        }

        // Stash the computed expression (if any) onto the footer's
        // computed-columns table. INSERTs route through the per-row
        // evaluator to materialise the value from the resolved row.
        if (computedSqlFragment is not null)
        {
            _columnComputeds ??= new List<ColumnComputedV4>();
            _columnComputeds.Add(new ColumnComputedV4(checked((ushort)newIndex), computedSqlFragment));
        }

        // Pump _totalRowsWritten values into the new column's encoder.
        // For a plain ADD COLUMN, every existing row reads NULL. For an
        // ADD COLUMN ... GENERATED ... AS IDENTITY, existing rows get
        // sequential counter values and the writer's identity state is
        // initialised so subsequent INSERTs continue past the backfill.
        // Pages flush automatically as the encoder fills.
        if (identityForNewColumn is not null)
        {
            if (_identityColumnIndex >= 0)
            {
                throw new InvalidOperationException(
                    $"AddColumn: cannot add IDENTITY column '{column.Name}' because the file " +
                    "already carries an IDENTITY column at footer index " + _identityColumnIndex +
                    ". Drop the existing IDENTITY column first.");
            }

            long counter = identityForNewColumn.Seed;
            for (long row = 0; row < _totalRowsWritten; row++)
            {
                DataValue value = BuildIdentityValue(column.Kind, counter, column.Name);
                _encoders[newIndex].Append(value, store: null, sidecar: null);
                if (_encoders[newIndex].IsFull)
                {
                    FlushPage(newIndex);
                }
                counter = checked(counter + identityForNewColumn.Step);
            }

            _identityColumnIndex = checked((short)newIndex);
            _identitySeed = identityForNewColumn.Seed;
            _identityStep = identityForNewColumn.Step;
            _identityAcceptUserValues = identityForNewColumn.AcceptUserValues;
            _identityNextValue = counter;
        }
        else
        {
            DataValue nullValue = DataValue.Null(column.Kind);
            for (long row = 0; row < _totalRowsWritten; row++)
            {
                _encoders[newIndex].Append(nullValue, store: null, sidecar: null);
                if (_encoders[newIndex].IsFull)
                {
                    FlushPage(newIndex);
                }
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="DataValue"/> carrying <paramref name="counterValue"/>
    /// in the target IDENTITY column's <see cref="DataKind"/>. Throws
    /// <see cref="OverflowException"/> when the counter has run past the
    /// kind's range — surfaces as a clean error during ALTER ADD IDENTITY
    /// backfill rather than a silent wrap.
    /// </summary>
    private static DataValue BuildIdentityValue(DataKind kind, long counterValue, string columnName)
    {
        try
        {
            return kind switch
            {
                DataKind.Int8 => DataValue.FromInt8(checked((sbyte)counterValue)),
                DataKind.Int16 => DataValue.FromInt16(checked((short)counterValue)),
                DataKind.Int32 => DataValue.FromInt32(checked((int)counterValue)),
                DataKind.Int64 => DataValue.FromInt64(counterValue),
                DataKind.UInt8 => DataValue.FromUInt8(checked((byte)counterValue)),
                DataKind.UInt16 => DataValue.FromUInt16(checked((ushort)counterValue)),
                DataKind.UInt32 => DataValue.FromUInt32(checked((uint)counterValue)),
                DataKind.UInt64 => DataValue.FromUInt64(checked((ulong)counterValue)),
                _ => throw new InvalidOperationException(
                    $"IDENTITY column '{columnName}': kind {kind} is not a supported integer kind."),
            };
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"IDENTITY column '{columnName}': counter value {counterValue} does not fit in {kind} " +
                "during ALTER ADD IDENTITY backfill. Choose a kind with wider range or a smaller seed/step.");
        }
    }

    /// <summary>
    /// One-shot helper that creates a fresh, empty <c>.datum</c> file
    /// at <paramref name="datumPath"/> with the given column schema and
    /// zero rows. Used by <c>CREATE TABLE</c> to materialise a table
    /// before any rows are inserted.
    /// </summary>
    /// <remarks>
    /// Overwrites any existing file at the path. The file is fully
    /// finalised on return — readers see a valid header + footer + tail
    /// describing an empty 0-row table whose columns match the input
    /// descriptors.
    /// </remarks>
    public static void CreateEmpty(string datumPath, IReadOnlyList<ColumnDescriptorV2> columns) =>
        CreateEmpty(datumPath, columns, columnDefaults: null, identity: null, primaryKeyColumnIndices: null);

    /// <summary>
    /// As <see cref="CreateEmpty(string, IReadOnlyList{ColumnDescriptorV2})"/>,
    /// but stamps a per-column <c>DEFAULT</c> literal table into the
    /// footer prologue. Used by <c>CREATE TABLE</c> to persist defaults
    /// alongside the table itself so opening the file is enough to see
    /// them — no separate catalog round-trip needed.
    /// </summary>
    public static void CreateEmpty(
        string datumPath,
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults) =>
        CreateEmpty(datumPath, columns, columnDefaults, identity: null, primaryKeyColumnIndices: null);

    /// <summary>
    /// As <see cref="CreateEmpty(string, IReadOnlyList{ColumnDescriptorV2}, IReadOnlyList{ColumnDefaultV4}?)"/>,
    /// but additionally stamps an <c>IDENTITY(seed, step)</c> spec into
    /// the footer prologue. The starting next-value equals
    /// <see cref="IdentityWriterSpec.Seed"/>; subsequent commits advance
    /// it via <see cref="UpdateIdentityNextValue"/>.
    /// </summary>
    public static void CreateEmpty(
        string datumPath,
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity) =>
        CreateEmpty(datumPath, columns, columnDefaults, identity, primaryKeyColumnIndices: null);

    /// <summary>
    /// As <see cref="CreateEmpty(string, IReadOnlyList{ColumnDescriptorV2}, IReadOnlyList{ColumnDefaultV4}?, IdentityWriterSpec?)"/>,
    /// but additionally stamps a PRIMARY KEY column-index list into the
    /// footer prologue. PK validation (column existence, total key
    /// size ≤ 16 bytes) happens at the catalog layer; the writer
    /// treats the list as opaque payload.
    /// </summary>
    public static void CreateEmpty(
        string datumPath,
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity,
        IReadOnlyList<ushort>? primaryKeyColumnIndices) =>
        CreateEmpty(datumPath, columns, columnDefaults, identity, primaryKeyColumnIndices, columnComputeds: null);

    /// <summary>
    /// Full-fidelity overload of <c>CreateEmpty</c> that additionally accepts
    /// computed columns. The catalog passes a non-null
    /// <paramref name="columnComputeds"/> when at least one column is
    /// <c>GENERATED ALWAYS AS</c>; the writer persists the SQL fragments in
    /// the footer's optional computed-columns block.
    /// </summary>
    public static void CreateEmpty(
        string datumPath,
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity,
        IReadOnlyList<ushort>? primaryKeyColumnIndices,
        IReadOnlyList<ColumnComputedV4>? columnComputeds) =>
        CreateEmpty(
            datumPath, columns, columnDefaults, identity, primaryKeyColumnIndices,
            columnComputeds, schemaColumns: null, sidecarPath: null);

    /// <summary>
    /// Full-fidelity overload of <c>CreateEmpty</c> that additionally
    /// persists declared struct column shapes. When any entry in
    /// <paramref name="schemaColumns"/> carries struct <see cref="ColumnInfo.Fields"/>,
    /// the writer opens <paramref name="sidecarPath"/>, interns the shapes
    /// into a fresh registry, and finalizes with a type table + per-column
    /// <c>StructTypeId</c>s — the empty file is self-describing before the
    /// first row lands, so append sessions and cold reopens rebuild the
    /// schema's field lists from the file alone.
    /// </summary>
    public static void CreateEmpty(
        string datumPath,
        IReadOnlyList<ColumnDescriptorV2> columns,
        IReadOnlyList<ColumnDefaultV4>? columnDefaults,
        IdentityWriterSpec? identity,
        IReadOnlyList<ushort>? primaryKeyColumnIndices,
        IReadOnlyList<ColumnComputedV4>? columnComputeds,
        IReadOnlyList<ColumnInfo>? schemaColumns,
        string? sidecarPath)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "CreateEmpty requires at least one column.", nameof(columns));
        }

        bool hasStructShapes = false;
        if (schemaColumns is not null)
        {
            foreach (ColumnInfo column in schemaColumns)
            {
                if (column.Kind == DataKind.Struct && column.Fields is { Count: > 0 })
                {
                    hasStructShapes = true;
                    break;
                }
            }
        }
        if (hasStructShapes && sidecarPath is null)
        {
            throw new ArgumentException(
                "CreateEmpty received struct column shapes but no sidecar path; the type-table " +
                "descriptor blobs need a .datum-blob to land in.", nameof(sidecarPath));
        }

        // Sidecar only opens when struct shapes need descriptor blobs —
        // shape-free tables stay single-file until a value actually spills.
        using DatumFileWriterV2 writer = new(datumPath, hasStructShapes ? sidecarPath : null);
        writer.Initialize(columns, columnDefaults, identity, primaryKeyColumnIndices, columnComputeds);
        if (hasStructShapes)
        {
            writer.SetTypeRegistry(new TypeRegistry());
            for (int i = 0; i < schemaColumns!.Count; i++)
            {
                ColumnInfo column = schemaColumns[i];
                if (column.Kind == DataKind.Struct && column.Fields is { Count: > 0 } fields)
                {
                    writer.DeclareStructColumnShape(i, fields);
                }
            }
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Updates the running IDENTITY counter to be stamped at the next
    /// <see cref="FinalizeWriter"/>. Called by an <c>IAppendSession</c>
    /// after it has reserved values for IDENTITY-filled rows so the
    /// committed prologue carries the live next-value. Throws when the
    /// table has no IDENTITY column.
    /// </summary>
    public void UpdateIdentityNextValue(long newNextValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_identityColumnIndex < 0)
        {
            throw new InvalidOperationException(
                "UpdateIdentityNextValue called on a writer whose footer has no IDENTITY column.");
        }
        _identityNextValue = newNextValue;
    }

    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, adds
    /// <paramref name="column"/> with all-null backfill, and commits
    /// via tail flip. See <see cref="AddColumn(ColumnDescriptorV2)"/> for the
    /// session-scoped equivalent and constraints.
    /// </summary>
    /// <param name="datumPath">Path to the <c>.datum</c> file to mutate.</param>
    /// <param name="column">Column descriptor to add.</param>
    /// <param name="defaultSqlFragment">
    /// Optional SQL-fragment representation of the column's <c>DEFAULT</c>
    /// expression. When non-<see langword="null"/>, persists in the file's
    /// prologue defaults table so future INSERTs that omit this column
    /// auto-fill with the default. Existing rows always backfill as NULL
    /// regardless.
    /// </param>
    /// <param name="computedSqlFragment">
    /// Optional <c>GENERATED ALWAYS AS</c> expression as a SQL fragment.
    /// Persists in the footer's computed-columns block; subsequent INSERTs
    /// evaluate the expression per row instead of accepting an explicit
    /// value for this column.
    /// </param>
    /// <param name="identityForNewColumn">
    /// Optional <see cref="IdentityWriterSpec"/> when the new column is
    /// declared <c>GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY</c>. The
    /// writer backfills existing rows with sequential counter values
    /// (instead of NULL) and initialises the file's identity state so
    /// subsequent INSERTs continue past the backfill. The
    /// <see cref="IdentityWriterSpec.ColumnIndex"/> field is ignored —
    /// the writer assigns the real footer index itself.
    /// </param>
    public static void AddColumn(
        string datumPath,
        ColumnDescriptorV2 column,
        string? defaultSqlFragment = null,
        string? computedSqlFragment = null,
        IdentityWriterSpec? identityForNewColumn = null)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(column);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        writer.AddColumn(column, defaultSqlFragment, computedSqlFragment, identityForNewColumn);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Rewrites <paramref name="datumPath"/>'s footer with
    /// <paramref name="pkColumnIndices"/> as the PRIMARY KEY column list.
    /// Pure footer mutation — no pages are written, no data is touched.
    /// Generation bumps by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by <c>ALTER TABLE … ADD COLUMN … PRIMARY KEY</c> after the
    /// column has been added (with IDENTITY backfill) and the
    /// <c>.datum-pkindex</c> sidecar has been built from the populated
    /// column. Flipping the footer's <c>PrimaryKeyColumnIndices</c> is
    /// the final commit step.
    /// </para>
    /// <para>
    /// Rejects the call when the file already has a non-empty
    /// PRIMARY KEY — only one PK per table is supported, and changing it
    /// would invalidate the existing <c>.datum-pkindex</c> sidecar.
    /// </para>
    /// </remarks>
    public static void SetPrimaryKey(string datumPath, IReadOnlyList<ushort> pkColumnIndices)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(pkColumnIndices);
        if (pkColumnIndices.Count == 0)
        {
            throw new ArgumentException(
                "SetPrimaryKey requires at least one column index.", nameof(pkColumnIndices));
        }

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        if (writer._primaryKeyColumnIndices is { Length: > 0 } existing)
        {
            throw new InvalidOperationException(
                $"SetPrimaryKey: file already has a PRIMARY KEY ({existing.Length} column(s)). " +
                "Changing an existing PK is not supported.");
        }

        // Validate every index is in range of the live column count so
        // a malformed call can't stamp an invalid footer.
        for (int i = 0; i < pkColumnIndices.Count; i++)
        {
            ushort idx = pkColumnIndices[i];
            if (idx >= writer._columns!.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkColumnIndices),
                    $"PRIMARY KEY index {idx} is out of range for a file with {writer._columns.Length} columns.");
            }
        }

        ushort[] indices = new ushort[pkColumnIndices.Count];
        for (int i = 0; i < pkColumnIndices.Count; i++) indices[i] = pkColumnIndices[i];
        writer._primaryKeyColumnIndices = indices;

        // We deliberately don't flip descriptor.IsNullable=false here even
        // though PK columns are conventionally NOT NULL. The on-disk pages
        // for an ALTER-added column were written by AddColumn with a null
        // bitmap (because the column had to be nullable to accept the
        // missing-value backfill for historical rows pre-IDENTITY pump).
        // Flipping IsNullable post-hoc would tell the decoder to skip the
        // null bitmap and shift every payload read by a byte. The schema-
        // build path overrides Nullable=false for PK columns at read time
        // (see ColumnInfo construction in DatumFileTableProviderV2.OpenSnapshot).

        writer.FinalizeWriter();
    }

    /// <summary>
    /// Rewrites <paramref name="datumPath"/>'s footer with an empty PRIMARY
    /// KEY column list (i.e., clears any prior PK). Pure footer mutation —
    /// no pages are written, no data is touched. Generation bumps by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by <c>ALTER TABLE … DROP CONSTRAINT &lt;table&gt;_pkey</c>. The
    /// caller is expected to delete the <c>.datum-pkindex</c> sidecar before
    /// or after this call — the writer does not touch the sidecar file
    /// (it doesn't own it; the provider does).
    /// </para>
    /// <para>
    /// No-op when the file already has no PK. Does NOT restore the prior
    /// column descriptor's IsNullable flag — descriptors describe wire
    /// format, and the on-disk pages were written according to whatever
    /// IsNullable was at write time. The schema-build path overrides
    /// Nullable=false for PK columns only when PrimaryKeyColumnIndices is
    /// non-empty; once cleared, the column's Nullable reverts to whatever
    /// the descriptor says (which is the right PG-equivalent semantics).
    /// </para>
    /// </remarks>
    public static void ClearPrimaryKey(string datumPath)
    {
        ArgumentNullException.ThrowIfNull(datumPath);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        if (writer._primaryKeyColumnIndices is null || writer._primaryKeyColumnIndices.Length == 0)
        {
            // No-op — finalize anyway to keep the call observable as a
            // generation bump, matching the SetPrimaryKey shape.
            writer.FinalizeWriter();
            return;
        }

        writer._primaryKeyColumnIndices = null;
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Clears the table's IDENTITY column state in <paramref name="datumPath"/>'s
    /// footer. Pure footer mutation — no pages are written, no data is
    /// touched. Existing rows keep their stored identity values; the
    /// column simply loses its auto-increment behavior for future INSERTs
    /// and becomes a plain nullable column from the runtime's point of
    /// view. Generation bumps by one.
    /// </summary>
    /// <remarks>
    /// No-op when the file already has no IDENTITY column. Used by
    /// <c>ALTER TABLE … ALTER COLUMN c DROP IDENTITY</c>.
    /// </remarks>
    public static void ClearIdentity(string datumPath)
    {
        ArgumentNullException.ThrowIfNull(datumPath);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        writer._identityColumnIndex = -1;
        writer._identitySeed = 0;
        writer._identityStep = 0;
        writer._identityNextValue = 0;
        writer._identityAcceptUserValues = false;
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Returns the footer column index of <paramref name="columnName"/>,
    /// or <see langword="null"/> when no live column matches. Tombstoned
    /// columns are skipped. Helper for footer-mutation entry points that
    /// take a column name from the user.
    /// </summary>
    private static int? FindLiveColumnIndex(DatumFileWriterV2 writer, string columnName)
    {
        for (int i = 0; i < writer._columns!.Length; i++)
        {
            ColumnDescriptorV2 d = writer._columns[i];
            if (d.IsTombstoned) continue;
            if (string.Equals(d.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>
    /// Flips <paramref name="columnName"/>'s descriptor flag <c>IsNullable</c>
    /// to <see langword="true"/> in <paramref name="datumPath"/>. Pure
    /// footer mutation. Historical pages keep their stored bytes; the
    /// per-page <c>HasNullBitmap</c> flag on each <see cref="PageDescriptorV2"/>
    /// continues to drive decoding for those pages, while pages flushed
    /// after this call carry a null bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No-op when the column is already nullable. Throws when the column
    /// doesn't exist (caller validates upstream — this is a safety net).
    /// </para>
    /// <para>
    /// The reverse operation — tightening a nullable column to NOT NULL —
    /// is intentionally unsupported here because it requires scanning
    /// existing rows to validate that no NULLs are present.
    /// </para>
    /// </remarks>
    public static void ClearColumnNotNull(string datumPath, string columnName)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnName);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        int? columnIndex = FindLiveColumnIndex(writer, columnName);
        if (columnIndex is null)
        {
            throw new InvalidOperationException(
                $"ClearColumnNotNull: column '{columnName}' does not exist on '{datumPath}'.");
        }

        ColumnDescriptorV2 prior = writer._columns![columnIndex.Value];
        if (!prior.IsNullable)
        {
            writer._columns[columnIndex.Value] = prior with { IsNullable = true };
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Flips <paramref name="columnName"/>'s descriptor flag <c>IsNullable</c>
    /// to <see langword="false"/> in <paramref name="datumPath"/>. Pure
    /// footer mutation. Historical pages keep their stored bytes —
    /// pages written while the column was nullable retain their
    /// (now-redundant) null bitmap, recorded per-page in
    /// <see cref="PageDescriptorV2.HasNullBitmap"/>; pages flushed after
    /// this call carry no bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No-op when the column is already NOT NULL. Throws when the column
    /// doesn't exist.
    /// </para>
    /// <para>
    /// The caller is responsible for scanning the column to verify no
    /// NULLs exist before invoking — this method does no validation.
    /// (Validation lives in the provider so it can use the catalog's
    /// scan path; the writer only owns the footer mutation.)
    /// </para>
    /// </remarks>
    public static void SetColumnNotNull(string datumPath, string columnName)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnName);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        int? columnIndex = FindLiveColumnIndex(writer, columnName);
        if (columnIndex is null)
        {
            throw new InvalidOperationException(
                $"SetColumnNotNull: column '{columnName}' does not exist on '{datumPath}'.");
        }

        ColumnDescriptorV2 prior = writer._columns![columnIndex.Value];
        if (prior.IsNullable)
        {
            writer._columns[columnIndex.Value] = prior with { IsNullable = false };
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Removes <paramref name="columnName"/>'s entry from the footer's
    /// defaults table in <paramref name="datumPath"/>. Pure footer
    /// mutation. Existing rows are unaffected; future INSERTs that omit
    /// the column will store NULL instead of the previously-configured
    /// default. Generation bumps by one.
    /// </summary>
    /// <remarks>
    /// No-op when the column has no default. Throws when the column does
    /// not exist (the catalog validates the column-exists precondition
    /// before calling, so this is a safety net only).
    /// </remarks>
    public static void ClearColumnDefault(string datumPath, string columnName)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnName);

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);

        int? columnIndex = FindLiveColumnIndex(writer, columnName);
        if (columnIndex is null)
        {
            throw new InvalidOperationException(
                $"ClearColumnDefault: column '{columnName}' does not exist on '{datumPath}'.");
        }

        if (writer._columnDefaults is { Count: > 0 })
        {
            ushort target = checked((ushort)columnIndex.Value);
            writer._columnDefaults.RemoveAll(d => d.ColumnIndex == target);
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// One-shot helper for batched column additions in a single
    /// commit. Generation bumps by one regardless of how many columns
    /// are added.
    /// </summary>
    public static void AddColumns(string datumPath, IReadOnlyList<ColumnDescriptorV2> columns)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);
        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (ColumnDescriptorV2 column in columns)
        {
            writer.AddColumn(column);
        }
        writer.FinalizeWriter();
    }


    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, marks
    /// <paramref name="columnName"/> as tombstoned, and commits via
    /// tail flip. Resolves the sidecar path from the source filename
    /// when the file declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>;
    /// callers wanting non-default sidecar placement should use
    /// <see cref="OpenForAppend"/> + <see cref="MarkColumnTombstoned"/> +
    /// <see cref="FinalizeWriter"/> directly.
    /// </summary>
    public static void DropColumn(string datumPath, string columnName)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnName);

        // Auto-resolve sidecar from convention if the file uses one.
        // We can determine that without opening for write by peeking
        // at the header flags through a read-only handle.
        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        writer.MarkColumnTombstoned(columnName);
        writer.FinalizeWriter();
    }


    /// <summary>
    /// One-shot helper for batched drops in a single commit.
    /// Equivalent to opening once, marking all named columns, and
    /// finalizing — generation increments by one regardless of how
    /// many columns are dropped in the call.
    /// </summary>
    public static void DropColumns(string datumPath, IReadOnlyList<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(columnNames);
        if (columnNames.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (string name in columnNames)
        {
            writer.MarkColumnTombstoned(name);
        }
        writer.FinalizeWriter();
    }
}
