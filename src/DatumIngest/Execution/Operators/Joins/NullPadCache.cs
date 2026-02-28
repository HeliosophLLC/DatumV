using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Caches a single null-padded <see cref="Row"/> for a join's outer-side
/// fallback. Encapsulates the lazy-create / use-many-times / pool-return
/// lifecycle that every join path repeats once per side.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// NullPadCache nullBuild = new(pool);
/// // ... per-row:
/// Row pad = nullBuild.GetOrCreate(buildRows[0]);
/// // ... finally:
/// nullBuild.Return();
/// </code>
/// </para>
/// <para>
/// <see cref="Initialize"/> is a non-lazy variant for sites that know the
/// template up front (e.g. the parallel probe path that picks the template
/// before fanning out workers).
/// </para>
/// </remarks>
internal sealed class NullPadCache
{
    private readonly Pool _pool;
    private Row? _row;

    public NullPadCache(Pool pool)
    {
        _pool = pool;
    }

    /// <summary>True once a null pad has been created.</summary>
    public bool HasValue => _row.HasValue;

    /// <summary>
    /// The cached null pad. Only valid after <see cref="GetOrCreate"/> or
    /// <see cref="Initialize"/> has been called.
    /// </summary>
    public Row Value => _row!.Value;

    /// <summary>
    /// Returns the cached null pad, creating it from <paramref name="template"/>
    /// on the first call. The cached row's <see cref="ColumnLookup"/> matches
    /// the template — only the values are nulled.
    /// </summary>
    public Row GetOrCreate(Row template)
    {
        _row ??= Create(template, _pool);
        return _row.Value;
    }

    /// <summary>
    /// Eagerly creates the null pad. No-op if already initialized. Useful when
    /// the template is known up front (e.g. before fanning out workers that
    /// will only read <see cref="Value"/>).
    /// </summary>
    public void Initialize(Row template)
    {
        _row ??= Create(template, _pool);
    }

    /// <summary>
    /// Returns the cached null pad's backing array to the pool. Idempotent.
    /// </summary>
    public void Return()
    {
        if (_row is not null)
        {
            _pool.ReturnRow(_row.Value);
            _row = null;
        }
    }

    /// <summary>
    /// Builds a null-padded row sharing <paramref name="template"/>'s
    /// <see cref="ColumnLookup"/> but with every value set to typed-null.
    /// The backing <see cref="DataValue"/>[] is rented from <paramref name="pool"/>;
    /// callers must release it via <see cref="Pool.ReturnRow"/>.
    /// </summary>
    /// <remarks>
    /// Exposed as a static helper for one-off null rows outside the caching
    /// pattern. Inside a join pipeline prefer the instance methods so the
    /// cache + return lifecycle is consistent.
    /// </remarks>
    public static Row Create(Row template, Pool pool)
    {
        DataValue[] values = pool.RentDataValues(template.FieldCount);
        for (int index = 0; index < template.FieldCount; index++)
        {
            values[index] = DataValue.Null(template[index].Kind);
        }
        return new Row(template.ColumnLookup, values);
    }
}
