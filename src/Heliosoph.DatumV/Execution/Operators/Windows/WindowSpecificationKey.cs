using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators.Windows;

/// <summary>
/// Structural-equality key for <see cref="WindowSpecification"/>, used by the
/// blocking window-shaped operators (<see cref="WindowOperator"/> and
/// <see cref="FoldScanOperator"/>) to group columns that share the same OVER
/// clause into one partition + sort + compute pass.
/// </summary>
/// <remarks>
/// The hash mixes <see cref="WindowSpecification.PartitionBy"/>,
/// <see cref="WindowSpecification.OrderBy"/>, and
/// <see cref="WindowSpecification.Frame"/>. Including Frame is strictly correct
/// even for operators that don't use frames today (FoldScan never sets it) —
/// it's a no-op for null and protects against future frame-aware variants
/// silently coalescing groups that should stay separate.
/// </remarks>
internal sealed class WindowSpecificationKey : IEquatable<WindowSpecificationKey>
{
    private readonly WindowSpecification _spec;
    private readonly int _hashCode;

    public WindowSpecificationKey(WindowSpecification spec)
    {
        _spec = spec;
        _hashCode = ComputeHash(spec);
    }

    public WindowSpecification Specification => _spec;

    public bool Equals(WindowSpecificationKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Equals(_spec, other._spec);
    }

    public override bool Equals(object? other) => other is WindowSpecificationKey key && Equals(key);

    public override int GetHashCode() => _hashCode;

    private static int ComputeHash(WindowSpecification spec)
    {
        HashCode hash = new();
        if (spec.PartitionBy is not null)
        {
            foreach (Expression expression in spec.PartitionBy)
            {
                hash.Add(expression);
            }
        }
        if (spec.OrderBy is not null)
        {
            foreach (OrderByItem item in spec.OrderBy)
            {
                hash.Add(item);
            }
        }
        if (spec.Frame is not null)
        {
            hash.Add(spec.Frame);
        }
        return hash.ToHashCode();
    }
}
