namespace DatumIngest.Manifest;

/// <summary>
/// Classifies a built-in function by its operational domain, enabling
/// grouped display in the REPL, structured filtering in gRPC clients,
/// and categorized autocomplete in the language server.
/// </summary>
public enum FunctionCategory
{
    /// <summary>Text manipulation, case conversion, search, and path utilities.</summary>
    String,

    /// <summary>UUID generation and inspection.</summary>
    Uuid,

    /// <summary>Date, time, duration, and timestamp construction, extraction, and arithmetic.</summary>
    Temporal,

    /// <summary>Arithmetic, rounding, powers, roots, logarithms, trigonometry, and constants.</summary>
    Numeric,

    /// <summary>ML activation functions (sigmoid, ReLU, GELU, etc.), softmax, and L2 normalization.</summary>
    Activation,

    /// <summary>Vector and tensor operations: reductions, manipulation, distance, similarity, and introspection.</summary>
    Vector,

    /// <summary>Image metadata, loading, transforms, analysis, and perceptual hashing.</summary>
    Image,

    /// <summary>UUID generation/inspection, cryptographic hashing (MD5/SHA/CRC), and base64/hex encoding.</summary>
    Encoding,

    /// <summary>JSON path access, existence testing, and array inspection.</summary>
    Json,

    /// <summary>Explicit type conversion between data kinds.</summary>
    Conversion,

    /// <summary>General-purpose conditional, null-handling, and byte manipulation functions.</summary>
    Utility,

    /// <summary>
    /// Array-based operations.
    /// </summary>
    Array,

    /// <summary>File path manipulation: filename, extension, directory, and path concatenation.</summary>
    File,

    /// <summary>Table-valued functions that produce multiple rows (used in FROM/JOIN clauses).</summary>
    Table,

    /// <summary>Aggregate functions that reduce multiple rows into a single result (COUNT, SUM, AVG, MIN, MAX).</summary>
    Aggregate,

    /// <summary>Window (analytical) functions that compute a value per row over a partition (ROW_NUMBER, RANK, LAG, LEAD, etc.).</summary>
    Window,

    /// <summary>Spatial / geometric: point construction, component access, distance, vector math.</summary>
    Spatial,
}
