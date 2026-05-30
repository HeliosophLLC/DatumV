using System.Collections.Generic;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Parsed, type-resolved option bag from a COPY statement's trailing
/// <c>(...)</c> block. Keys are case-insensitive. Values are plain
/// CLR scalars — <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/> — produced by evaluating the
/// option's literal expression at plan time. Format implementations
/// pull the keys they understand and surface a clear error for unknown
/// keys so typos do not silently disappear.
/// </summary>
public sealed class ExportOptions
{
    private readonly Dictionary<string, object> _values;

    /// <summary>
    /// Creates a new <see cref="ExportOptions"/> from a flat dictionary.
    /// Keys are stored case-insensitively.
    /// </summary>
    public ExportOptions(IReadOnlyDictionary<string, object> values)
    {
        _values = new(values, System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An empty option set — useful for tests and the implicit defaults path.</summary>
    public static ExportOptions Empty { get; } = new(new Dictionary<string, object>());

    /// <summary>The keys present in the option block, in canonical form.</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    /// <summary>True when <paramref name="key"/> was supplied in the option block.</summary>
    public bool Contains(string key) => _values.ContainsKey(key);

    /// <summary>
    /// Returns the option value as a string. Numeric and boolean values are
    /// stringified via <see cref="object.ToString"/> for forgiving lookup —
    /// callers that need typed access use <see cref="TryGetLong"/> or
    /// <see cref="TryGetBool"/>.
    /// </summary>
    public string? GetString(string key)
        => _values.TryGetValue(key, out object? v) ? v?.ToString() : null;

    /// <summary>True if <paramref name="key"/> resolves to a long integer.</summary>
    public bool TryGetLong(string key, out long value)
    {
        if (_values.TryGetValue(key, out object? raw))
        {
            switch (raw)
            {
                case long l: value = l; return true;
                case int i: value = i; return true;
                case double d when d == (long)d: value = (long)d; return true;
                case string s when long.TryParse(s, out long parsed): value = parsed; return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>True if <paramref name="key"/> resolves to a boolean.</summary>
    public bool TryGetBool(string key, out bool value)
    {
        if (_values.TryGetValue(key, out object? raw))
        {
            switch (raw)
            {
                case bool b: value = b; return true;
                case string s when bool.TryParse(s, out bool parsed): value = parsed; return true;
            }
        }
        value = false;
        return false;
    }
}
