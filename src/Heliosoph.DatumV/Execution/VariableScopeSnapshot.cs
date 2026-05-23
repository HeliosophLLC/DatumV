using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Captures the visible procedural variable bindings on an
/// <see cref="ExecutionContext"/> into a managed-typed snapshot. Used by
/// tests and debugging utilities that want to assert "after this batch,
/// <c>@x</c> was 5" without poking at <see cref="ValueRef"/> offsets or
/// reading from a soon-to-dispose <see cref="ExecutionContext.VariableStore"/>.
/// </summary>
internal static class VariableScopeSnapshot
{
    /// <summary>
    /// Walks every visible binding in
    /// <see cref="ExecutionContext.VariableScope"/> and materialises each
    /// value into managed form. Numerics → boxed numerics, booleans →
    /// <see cref="bool"/>, strings → <see cref="string"/>, NULLs →
    /// <see langword="null"/>; composite kinds (arrays, structs) return
    /// the raw <see cref="ValueRef"/> boxed so callers can inspect the
    /// shape directly. The snapshot is independent of
    /// <see cref="ExecutionContext.VariableStore"/> — safe to read after
    /// the context disposes.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Capture(ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<string, object?> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ValueRef> entry in context.VariableScope.EnumerateVisible())
        {
            snapshot[entry.Key] = Materialize(entry.Value);
        }
        return snapshot;
    }

    private static object? Materialize(ValueRef value)
    {
        if (value.IsNull) return null;
        if (value.IsArray) return value; // boxed for caller to inspect
        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.String => value.AsString(),
            _ => value, // boxed ValueRef — caller can stabilise / inspect manually
        };
    }
}
