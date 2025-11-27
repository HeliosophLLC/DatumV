using DatumIngest.Model;

namespace DatumIngest.Tests;

/// <summary>
/// Compile-only stubs for legacy <c>DataValue.FromArray</c> / <c>AsArray</c>
/// shapes that PR2 retired. The tests using these stubs are pre-existing
/// rot — they fail at runtime on the deprecated <c>new Row(string[], DataValue[])</c>
/// constructor's "DON'T USE" throw — so test bodies never execute. The stubs
/// preserve compile parity until the tests are rewritten alongside the
/// Row constructor restoration; deleting them now would conflate scope.
/// </summary>
internal static class LegacyArrayTestStubs
{
    /// <summary>Compile-only stub for the retired <c>DataValue.AsArray()</c>.</summary>
    public static DataValue[] AsArrayLegacyStub(this DataValue _) =>
        throw new InvalidOperationException(
            "DataValue.AsArray() is retired (PR2). Migrate the test to typed-array " +
            "accessors (AsStringArray / AsImageArray / AsStructArray / AsArraySpan<T>).");

    /// <summary>Compile-only stub for the retired <c>DataValue.AsArray(IValueStore)</c>.</summary>
    public static DataValue[] AsArrayLegacyStub(this DataValue _, IValueStore __) =>
        throw new InvalidOperationException(
            "DataValue.AsArray(store) is retired (PR2). Migrate the test to typed-array " +
            "accessors (AsStringArray / AsImageArray / AsStructArray / AsArraySpan<T>).");

    /// <summary>
    /// Compile-only stub for the retired <c>DataValue.FromArray(DataKind, DataValue[])</c>
    /// no-store overload. Returns a typed null array carrier — not a populated array,
    /// but enough to compile through the test's broken-Row prefix.
    /// </summary>
    public static DataValue FromArrayLegacyStub(DataKind elementKind, DataValue[] _) =>
        DataValue.NullArrayOf(elementKind);

    /// <summary>Same as above but with an <see cref="IValueStore"/>.</summary>
    public static DataValue FromArrayLegacyStub(DataKind elementKind, DataValue[] _, IValueStore __) =>
        DataValue.NullArrayOf(elementKind);
}
