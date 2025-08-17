namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// Classifies the expected cardinality relationship of a join candidate.
/// Derived from uniqueness scores on each side of the join.
/// </summary>
public enum JoinClassification
{
    /// <summary>Both sides have near-unique keys — one row matches at most one row.</summary>
    OneToOne,

    /// <summary>Left side has unique keys; right side may have duplicates.</summary>
    OneToMany,

    /// <summary>Right side has unique keys; left side may have duplicates.</summary>
    ManyToOne,

    /// <summary>Neither side has unique keys — risk of cartesian explosion.</summary>
    ManyToMany,
}
