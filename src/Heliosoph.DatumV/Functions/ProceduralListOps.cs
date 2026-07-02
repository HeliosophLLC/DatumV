using System.Runtime.InteropServices;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Execution helpers for the body-local <c>List&lt;T&gt;</c> accumulator
/// statements — <c>DECLARE @x List&lt;T&gt;</c>, <c>APPEND value TO @x</c>, and
/// <c>RESERVE n FOR @x</c>. Shared by both procedural statement executors
/// (<see cref="ProceduralModelFunction"/> and <see cref="ProceduralUdfFunction"/>)
/// so the two stay in lock-step.
/// </summary>
/// <remarks>
/// A <c>List&lt;T&gt;</c> is carried as a <see cref="ListBuilderValue"/> on a
/// <see cref="ValueRef"/>. The carrier is a reference type, so an in-place
/// <c>APPEND</c> mutates the same instance the <see cref="VariableScope"/> holds
/// — no write-back is needed.
/// </remarks>
internal static class ProceduralListOps
{
    /// <summary>
    /// If <paramref name="decl"/> declares a <c>List&lt;T&gt;</c>, binds a fresh
    /// empty accumulator in <paramref name="scope"/> and returns <c>true</c>;
    /// otherwise returns <c>false</c> so the caller runs its ordinary DECLARE
    /// path. A <c>List&lt;T&gt;</c> must be declared without an initializer.
    /// </summary>
    public static bool TryDeclareList(DeclareStatement decl, VariableScope scope)
    {
        if (decl.TypeName is null
            || !TypeAnnotationResolver.TryParseListBuilder(decl.TypeName, out DataKind elementKind))
        {
            return false;
        }
        else if (decl.Initializer is not null)
        {
            throw new ExecutionException(
                $"DECLARE @{decl.VariableName} {decl.TypeName}: a List accumulator is declared without an "
                + "initializer; populate it with RESERVE / APPEND. It freezes to an Array<T> when read.");
        }
        scope.Declare(decl.VariableName, ValueRef.FromListBuilder(new ListBuilderValue(elementKind)));
        return true;
    }

    /// <summary>
    /// Appends <paramref name="value"/> to the <c>List&lt;T&gt;</c> bound to
    /// <paramref name="target"/>. A scalar appends one element; a peer array of
    /// the same element kind appends all of its elements (concatenation). The
    /// value's kind must match the list's element kind.
    /// </summary>
    public static void Append(string target, ValueRef value, VariableScope scope, IValueStore store)
    {
        ListBuilderValue list = ResolveList(target, scope, "APPEND");

        // Appending one list to another means appending its elements.
        if (value.IsListBuilder)
        {
            value = value.FreezeToArray();
        }

        if (value.IsNull)
        {
            throw new ExecutionException($"APPEND TO @{target}: cannot append a null value to a List.");
        }

        if (value.IsArray)
        {
            // Peer-array concatenation requires the element kinds to match
            // exactly (per-element coercion is out of scope; the caller can
            // CAST the array). The element bytes are read with the real element
            // type — the inline-array path reports length in elements, so reading
            // a small Int32[2] as bytes would yield 2, not 8.
            if (value.Kind != list.ElementKind)
            {
                throw new ExecutionException(
                    $"APPEND TO @{target}: array element kind {value.Kind} does not match the list "
                    + $"element kind {list.ElementKind}. CAST the array to {list.ElementKind}[] first.");
            }
            AppendArrayElements(list, value, store);
        }
        else
        {
            // Coerce a scalar to the element kind. SQL literals narrow to the
            // smallest type (`1` is Int8), so `APPEND 1 TO List<Int32>` arrives
            // as Int8 — a permissive numeric assignment, matching DECLARE/SET.
            if (value.Kind != list.ElementKind)
            {
                if (!Scalar.CastFunction.TryCastCore(value, list.ElementKind, out ValueRef coerced))
                {
                    throw new ExecutionException(
                        $"APPEND TO @{target}: value kind {value.Kind} cannot be converted to the list "
                        + $"element kind {list.ElementKind}.");
                }
                value = coerced;
            }
            Span<byte> scalar = stackalloc byte[list.Stride];
            value.InlineDataValue.CopyInlineScalarBytes(scalar);
            list.AppendBytes(scalar);
        }
    }

    // Appends a peer array's element bytes to the list. The element kind picks
    // the typed span; the span itself comes from the array's managed payload
    // when it has one (array_slice / array_flatten / literals are off-arena) —
    // avoiding a per-append ToDataValue copy into the body arena — and falls
    // back to materialising arena/sidecar/inline-backed arrays. The kind→T
    // mapping mirrors ValueRef.FreezeToArray.
    private static void AppendArrayElements(ListBuilderValue list, ValueRef value, IValueStore store)
    {
        switch (list.ElementKind)
        {
            case DataKind.UInt8: AppendTyped<byte>(list, value, store); break;
            case DataKind.Int8: AppendTyped<sbyte>(list, value, store); break;
            case DataKind.Boolean: AppendTyped<bool>(list, value, store); break;
            case DataKind.UInt16: AppendTyped<ushort>(list, value, store); break;
            case DataKind.Int16: AppendTyped<short>(list, value, store); break;
            case DataKind.UInt32: AppendTyped<uint>(list, value, store); break;
            case DataKind.Int32 or DataKind.Date: AppendTyped<int>(list, value, store); break;
            case DataKind.Float32: AppendTyped<float>(list, value, store); break;
            case DataKind.UInt64: AppendTyped<ulong>(list, value, store); break;
            case DataKind.Int64 or DataKind.Timestamp or DataKind.TimestampTz
                or DataKind.Time or DataKind.Duration: AppendTyped<long>(list, value, store); break;
            case DataKind.Float64: AppendTyped<double>(list, value, store); break;
            default:
                throw new ExecutionException(
                    $"APPEND: appending an array to a List<{list.ElementKind}> is not supported.");
        }
    }

    // Reads the element span from the managed payload directly when present (no
    // arena write), otherwise materialises the arena/sidecar/inline array once.
    private static void AppendTyped<T>(ListBuilderValue list, ValueRef value, IValueStore store)
        where T : unmanaged
    {
        ReadOnlySpan<T> span = value.Materialized is T[] managed
            ? managed
            : value.ToDataValue(store).AsArraySpan<T>(store);
        list.AppendBytes(MemoryMarshal.AsBytes(span));
    }

    /// <summary>
    /// Pre-sizes the <c>List&lt;T&gt;</c> bound to <paramref name="target"/> to
    /// hold at least <paramref name="capacity"/> elements. A pure capacity hint;
    /// it does not change the list's length.
    /// </summary>
    public static void Reserve(string target, ValueRef capacity, VariableScope scope)
    {
        ListBuilderValue list = ResolveList(target, scope, "RESERVE");
        if (capacity.IsNull)
        {
            throw new ExecutionException($"RESERVE FOR @{target}: capacity must not be null.");
        }
        // Accept any integer kind (SQL literals narrow — `64` is Int8) by
        // coercing to Int64; a non-numeric value (e.g. a string) fails the cast.
        if (!Scalar.CastFunction.TryCastCore(capacity, DataKind.Int64, out ValueRef asInt64))
        {
            throw new ExecutionException(
                $"RESERVE FOR @{target}: capacity must be an integer, got {capacity.Kind}.");
        }
        long n = asInt64.InlineDataValue.AsInt64();
        list.Reserve(n > int.MaxValue ? int.MaxValue : (int)Math.Max(0, n));
    }

    private static ListBuilderValue ResolveList(string target, VariableScope scope, string op)
    {
        if (!scope.TryGet(target, out ValueRef bound))
        {
            throw new ExecutionException(
                $"{op}: variable @{target} is not declared in this scope. {op} is only valid on a "
                + "List<T> accumulator declared via DECLARE @x List<T>.");
        }
        if (!bound.IsListBuilder)
        {
            throw new ExecutionException(
                $"{op} target @{target} is a {bound.Kind}, not a List<T>. {op} is only valid on a "
                + "List<T> accumulator.");
        }
        return bound.AsListBuilder();
    }
}
