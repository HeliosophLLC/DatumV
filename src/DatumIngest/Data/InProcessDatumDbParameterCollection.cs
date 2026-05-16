using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Data;

/// <summary>
/// Named-parameter bag for an <see cref="InProcessDatumDbCommand"/>. Wraps a
/// case-sensitive <see cref="Dictionary{TKey, TValue}"/> the command hands to
/// <see cref="ParameterBinder"/> at execute time. Parameter names match the
/// SQL <c>$name</c> placeholders verbatim — no <c>@</c> prefix and no
/// case-folding.
/// </summary>
public sealed class InProcessDatumDbParameterCollection
{
    private readonly Dictionary<string, ParameterValue> _values =
        new(StringComparer.Ordinal);

    /// <summary>Number of bound parameters.</summary>
    public int Count => _values.Count;

    /// <summary>Adds or replaces a named <see cref="ParameterValue"/>.</summary>
    public InProcessDatumDbParameterCollection Add(string name, ParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
        return this;
    }

    /// <summary>Adds or replaces a named scalar <see cref="DataValue"/> (wrapped as an <see cref="InlineParameter"/>).</summary>
    public InProcessDatumDbParameterCollection Add(string name, DataValue value)
        => Add(name, new InlineParameter(value));

    /// <summary>Adds or replaces a string parameter. Pass <see langword="null"/> to bind SQL NULL.</summary>
    public InProcessDatumDbParameterCollection AddString(string name, string? value)
        => value is null
            ? Add(name, DataValue.Null(DataKind.String))
            : Add(name, new StringParameter(value));

    /// <summary>Adds or replaces a 64-bit integer parameter. Pass <see langword="null"/> to bind SQL NULL.</summary>
    public InProcessDatumDbParameterCollection AddInt64(string name, long? value)
        => Add(name, value is { } v ? DataValue.FromInt64(v) : DataValue.Null(DataKind.Int64));

    /// <summary>Adds or replaces a 32-bit integer parameter.</summary>
    public InProcessDatumDbParameterCollection AddInt32(string name, int? value)
        => Add(name, value is { } v ? DataValue.FromInt32(v) : DataValue.Null(DataKind.Int32));

    /// <summary>Adds or replaces a boolean parameter.</summary>
    public InProcessDatumDbParameterCollection AddBoolean(string name, bool? value)
        => Add(name, value is { } v ? DataValue.FromBoolean(v) : DataValue.Null(DataKind.Boolean));

    /// <summary>Adds or replaces a 64-bit floating-point parameter.</summary>
    public InProcessDatumDbParameterCollection AddFloat64(string name, double? value)
        => Add(name, value is { } v ? DataValue.FromFloat64(v) : DataValue.Null(DataKind.Float64));

    /// <summary>Removes the binding for <paramref name="name"/> if present.</summary>
    public bool Remove(string name) => _values.Remove(name);

    /// <summary>Clears all bindings.</summary>
    public void Clear() => _values.Clear();

    /// <summary>
    /// Snapshot the collection as a read-only dictionary suitable for
    /// <see cref="ParameterBinder.Bind(DatumIngest.Parsing.Ast.Statement, IReadOnlyDictionary{string, ParameterValue})"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, ParameterValue> AsValueMap() => _values;
}
