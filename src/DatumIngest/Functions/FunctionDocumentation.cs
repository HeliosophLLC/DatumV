namespace DatumIngest.Functions;

using DatumIngest.Manifest;

/// <summary>
/// Curated documentation metadata for built-in functions, providing parameter names,
/// descriptions, and return type information for language server autocomplete and hover.
/// This is a static registry that does not require modifying individual function classes.
/// </summary>
public static class FunctionDocumentation
{
    private static readonly Dictionary<string, FunctionSignature> Entries = new(StringComparer.OrdinalIgnoreCase);

    static FunctionDocumentation()
    {
        RegisterAll();
    }

    /// <summary>
    /// Looks up documentation for a function by name.
    /// </summary>
    /// <returns>The function signature with documentation, or null if not found.</returns>
    public static FunctionSignature? TryGet(string functionName)
    {
        Entries.TryGetValue(functionName, out FunctionSignature? signature);
        return signature;
    }

    /// <summary>
    /// Returns all documented function signatures.
    /// </summary>
    public static IEnumerable<FunctionSignature> All => Entries.Values;

    private static void Register(FunctionSignature signature)
    {
        Entries[signature.Name] = signature;
    }

    private static ParameterSignature Parameter(string name, string kind, bool isOptional = false) =>
        new() { Name = name, Kind = kind, IsOptional = isOptional };

    private static void RegisterAll()
    {
        // ── Numeric/Array ──

        Register(new FunctionSignature
        {
            Name = "normalize",
            Parameters = [Parameter("value", "Float32"), Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Normalizes a value to [0, 1] given a known min/max range.",
            Category = FunctionCategory.Numeric,
        });
        Register(new FunctionSignature
        {
            Name = "clamp",
            Parameters = [Parameter("value", "Float32"), Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Clamps a value to the range [min, max].",
            Category = FunctionCategory.Numeric,
        });
        Register(new FunctionSignature
        {
            Name = "denormalize",
            Parameters = [Parameter("value", "Float32"), Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Maps a [0, 1] value back to the original [min, max] range.",
            Category = FunctionCategory.Numeric,
        });
        Register(new FunctionSignature
        {
            Name = "reshape",
            Parameters = [Parameter("tensor", "Tensor"), Parameter("dim1", "Float32"), Parameter("dim2", "Float32", isOptional: true)],
            ReturnType = "Tensor",
            Description = "Reinterprets the shape of a tensor without copying data. Element count must match.",
            Category = FunctionCategory.Vector,
        });

        // ── String ──

        Register(new FunctionSignature
        {
            Name = "len",
            Parameters = [Parameter("value", "String")],
            ReturnType = "Float32",
            Description = "Returns the character length of a string.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "mid",
            Parameters = [Parameter("value", "String"), Parameter("start", "Float32"), Parameter("length", "Float32")],
            ReturnType = "String",
            Description = "Extracts a substring starting at the given 1-based position with the specified length.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "substring",
            Parameters = [Parameter("value", "String"), Parameter("start", "Float32"), Parameter("length", "Float32", isOptional: true)],
            ReturnType = "String",
            Description = "Extracts a substring from a 0-based start position, optionally with a length.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "get_filename",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the file name (with extension) from a file path.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "get_file_extension",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the file extension (including the dot) from a file path.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "get_path",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the directory path from a file path.",
            Category = FunctionCategory.String,
        });
        Register(new FunctionSignature
        {
            Name = "regexp_extract",
            Parameters = [Parameter("input", "String"), Parameter("pattern", "String"), Parameter("group_index", "Float32", isOptional: true)],
            ReturnType = "String",
            Description = "Extracts the first substring matching a regular expression. With group_index (1-based), returns a specific capture group. Returns NULL if no match.",
            Category = FunctionCategory.String,
        });

        // ── Type Conversion ──

        Register(new FunctionSignature
        {
            Name = "cast",
            Parameters = [Parameter("value", "Any"), Parameter("target_type", "String")],
            ReturnType = null,
            Description = "Explicit type conversion between DataKind types. Target type is a DataKind name.",
            Category = FunctionCategory.Conversion,
        });
        Register(new FunctionSignature
        {
            Name = "to_epoch",
            Parameters = [Parameter("value", "DateTime")],
            ReturnType = "Float32",
            Description = "Converts a Date or DateTime to epoch seconds (float).",
            Category = FunctionCategory.Conversion,
        });
        Register(new FunctionSignature
        {
            Name = "date_part",
            Parameters = [Parameter("part", "String"), Parameter("value", "DateTime")],
            ReturnType = "Float32",
            Description = "Extracts a component (year, month, day, hour, minute, second) from a Date or DateTime.",
            Category = FunctionCategory.Conversion,
        });
        Register(new FunctionSignature
        {
            Name = "cyclical_encode",
            Parameters = [Parameter("value", "Float32"), Parameter("period", "Float32")],
            ReturnType = "Vector",
            Description = "Encodes a cyclic value as a [sin, cos] pair for ML features.",
            Category = FunctionCategory.Conversion,
        });

        // ── Date/Time — Extraction ──

        RegisterDateExtraction("year", "Extracts the year from a Date or DateTime.");
        RegisterDateExtraction("month", "Extracts the month (1–12) from a Date or DateTime.");
        RegisterDateExtraction("day", "Extracts the day of the month (1–31) from a Date or DateTime.");
        RegisterDateExtraction("hour", "Extracts the hour (0–23) from a DateTime. Returns 0 for Date inputs.");
        RegisterDateExtraction("minute", "Extracts the minute (0–59) from a DateTime. Returns 0 for Date inputs.");
        RegisterDateExtraction("second", "Extracts the second (0–59) from a DateTime. Returns 0 for Date inputs.");
        RegisterDateExtraction("quarter", "Extracts the quarter (1–4) from a Date or DateTime.");
        RegisterDateExtraction("dayofweek", "Returns the ISO day of week (1=Monday, 7=Sunday) from a Date or DateTime.");
        RegisterDateExtraction("dayofyear", "Returns the day of the year (1–366) from a Date or DateTime.");

        // ── Date/Time — Construction & Arithmetic ──

        Register(new FunctionSignature
        {
            Name = "now",
            Parameters = [],
            ReturnType = "DateTime",
            Description = "Returns the current UTC timestamp.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "make_date",
            Parameters = [Parameter("year", "Float32"), Parameter("month", "Float32"), Parameter("day", "Float32")],
            ReturnType = "Date",
            Description = "Constructs a Date from year, month, and day components.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "make_timestamp",
            Parameters = [Parameter("year", "Float32"), Parameter("month", "Float32"), Parameter("day", "Float32"), Parameter("hour", "Float32"), Parameter("minute", "Float32"), Parameter("second", "Float32")],
            ReturnType = "DateTime",
            Description = "Constructs a UTC DateTime from year, month, day, hour, minute, and second components.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "date_diff",
            Parameters = [Parameter("part", "String"), Parameter("start", "DateTime"), Parameter("end", "DateTime")],
            ReturnType = "Float32",
            Description = "Returns the number of date part boundaries between start and end.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "date_add",
            Parameters = [Parameter("part", "String"), Parameter("number", "Float32"), Parameter("date", "DateTime")],
            ReturnType = "DateTime",
            Description = "Adds the specified number of date part units to a date.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "date_trunc",
            Parameters = [Parameter("part", "String"), Parameter("date", "DateTime")],
            ReturnType = "DateTime",
            Description = "Truncates a date to the specified precision (e.g., month → first of month).",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "date_bucket",
            Parameters = [Parameter("part", "String"), Parameter("width", "Float32"), Parameter("date", "DateTime"), Parameter("origin", "DateTime", isOptional: true)],
            ReturnType = "DateTime",
            Description = "Buckets a date into fixed-width intervals of the specified date part.",
            Category = FunctionCategory.Temporal,
        });

        // ── Date/Time — Formatting & Probing ──

        Register(new FunctionSignature
        {
            Name = "strftime",
            Parameters = [Parameter("date", "DateTime"), Parameter("format", "String")],
            ReturnType = "String",
            Description = "Formats a Date or DateTime as a string using a .NET format string.",
            Category = FunctionCategory.Temporal,
        });
        Register(new FunctionSignature
        {
            Name = "is_date",
            Parameters = [Parameter("value", "String")],
            ReturnType = "Float32",
            Description = "Returns 1 if the string can be parsed as a date, 0 otherwise.",
            Category = FunctionCategory.Temporal,
        });

        // ── JSON ──

        Register(new FunctionSignature
        {
            Name = "json_value",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts a scalar value from a JSON document at the specified path.",
            Category = FunctionCategory.Json,
        });
        Register(new FunctionSignature
        {
            Name = "json_query",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "JsonValue",
            Description = "Extracts a JSON object or array from a JSON document at the specified path.",
            Category = FunctionCategory.Json,
        });
        Register(new FunctionSignature
        {
            Name = "json_exists",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "Float32",
            Description = "Returns 1 if the path exists in the JSON document, 0 otherwise.",
            Category = FunctionCategory.Json,
        });
        Register(new FunctionSignature
        {
            Name = "json_array_length",
            Parameters = [Parameter("json", "JsonValue")],
            ReturnType = "Float32",
            Description = "Returns the number of elements in a JSON array.",
            Category = FunctionCategory.Json,
        });

        // ── Math — Arithmetic ──

        RegisterUnary("abs", "Element-wise absolute value: abs(x) = |x|.");
        RegisterUnary("sign", "Returns the sign of the value: -1, 0, or 1.");
        RegisterUnary("negate", "Element-wise negation: negate(x) = -x.");
        RegisterBinary("mod", "value", "divisor", "Modulo (remainder after division).");
        RegisterBinary("add", "left", "right", "Element-wise addition.");
        RegisterBinary("subtract", "left", "right", "Element-wise subtraction.");
        RegisterBinary("multiply", "left", "right", "Element-wise multiplication.");
        RegisterBinary("divide", "left", "right", "Element-wise division.");

        // ── Math — Powers/Roots/Logs ──

        RegisterUnary("sqrt", "Element-wise square root.");
        RegisterUnary("cbrt", "Element-wise cube root.");
        RegisterUnary("square", "Element-wise square: square(x) = x².");
        RegisterUnary("exp", "Element-wise natural exponential: exp(x) = eˣ.");
        RegisterUnary("exp2", "Element-wise base-2 exponential: exp2(x) = 2ˣ.");
        RegisterUnary("ln", "Element-wise natural logarithm.");
        RegisterUnary("log2", "Element-wise base-2 logarithm.");
        RegisterUnary("log10", "Element-wise base-10 logarithm.");
        RegisterBinary("pow", "base", "exponent", "Element-wise power: pow(base, exponent).");
        RegisterBinary("log", "value", "base", "Logarithm with a specified base.");

        // ── Math — Trigonometric & Hyperbolic ──

        RegisterUnary("sin", "Element-wise sine (radians).");
        RegisterUnary("cos", "Element-wise cosine (radians).");
        RegisterUnary("tan", "Element-wise tangent (radians).");
        RegisterUnary("asin", "Element-wise arcsine (returns radians).");
        RegisterUnary("acos", "Element-wise arccosine (returns radians).");
        RegisterUnary("atan", "Element-wise arctangent (returns radians).");
        RegisterBinary("atan2", "y", "x", "Two-argument arctangent: atan2(y, x).");
        RegisterUnary("sinh", "Element-wise hyperbolic sine.");
        RegisterUnary("cosh", "Element-wise hyperbolic cosine.");
        RegisterUnary("tanh", "Element-wise hyperbolic tangent.");
        RegisterUnary("degrees", "Converts radians to degrees.");
        RegisterUnary("radians", "Converts degrees to radians.");
        Register(new FunctionSignature
        {
            Name = "pi",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Returns the constant π (3.14159...).",
            Category = FunctionCategory.Numeric,
        });
        Register(new FunctionSignature
        {
            Name = "euler",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Returns Euler's number e (2.71828...).",
            Category = FunctionCategory.Numeric,
        });

        // ── Math — Rounding & Quantization ──

        RegisterUnary("ceil", "Element-wise ceiling (rounds up to nearest integer).");
        RegisterUnary("floor", "Element-wise floor (rounds down to nearest integer).");
        RegisterUnary("truncate", "Element-wise truncation toward zero.");
        RegisterUnary("round", "Element-wise rounding to nearest integer.");
        RegisterBinary("quantize", "value", "step", "Quantizes to the nearest multiple of step.");
        RegisterBinary("bucketize", "value", "bucket_count", "Maps a [0, 1] value to a discrete bucket index.");
        Register(new FunctionSignature
        {
            Name = "clip",
            Parameters = [Parameter("value", "Float32"), Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Clips a value to the range [min, max]. Alias for clamp.",
            Category = FunctionCategory.Numeric,
        });

        // ── Math — ML Activations ──

        RegisterUnary("sigmoid", "Sigmoid activation: σ(x) = 1 / (1 + e⁻ˣ).", FunctionCategory.Activation);
        RegisterUnary("relu", "Rectified linear unit: relu(x) = max(0, x).", FunctionCategory.Activation);
        RegisterUnary("selu", "Scaled exponential linear unit.", FunctionCategory.Activation);
        RegisterUnary("gelu", "Gaussian error linear unit.", FunctionCategory.Activation);
        RegisterUnary("swish", "Swish activation: swish(x) = x · σ(x).", FunctionCategory.Activation);
        RegisterUnary("softplus", "Softplus: softplus(x) = ln(1 + eˣ).", FunctionCategory.Activation);
        RegisterUnary("softsign", "Softsign: softsign(x) = x / (1 + |x|).", FunctionCategory.Activation);
        RegisterUnary("mish", "Mish activation: mish(x) = x · tanh(softplus(x)).", FunctionCategory.Activation);
        RegisterUnary("hard_sigmoid", "Piecewise-linear approximation of sigmoid.", FunctionCategory.Activation);
        RegisterUnary("hard_swish", "Piecewise-linear approximation of swish.", FunctionCategory.Activation);
        Register(new FunctionSignature
        {
            Name = "leaky_relu",
            Parameters = [Parameter("value", "Float32"), Parameter("alpha", "Float32", isOptional: true)],
            ReturnType = "Float32",
            Description = "Leaky ReLU with configurable negative slope (default α = 0.01).",
            Category = FunctionCategory.Activation,
        });
        Register(new FunctionSignature
        {
            Name = "elu",
            Parameters = [Parameter("value", "Float32"), Parameter("alpha", "Float32", isOptional: true)],
            ReturnType = "Float32",
            Description = "Exponential linear unit with configurable α (default α = 1.0).",
            Category = FunctionCategory.Activation,
        });

        // ── Math — Softmax & Normalization ──

        Register(new FunctionSignature
        {
            Name = "softmax",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Applies softmax normalization: each element becomes exp(xᵢ) / Σexp(xⱼ).",
            Category = FunctionCategory.Activation,
        });
        Register(new FunctionSignature
        {
            Name = "log_softmax",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Log of softmax: numerically stable version of ln(softmax(x)).",
            Category = FunctionCategory.Activation,
        });
        Register(new FunctionSignature
        {
            Name = "l2_normalize",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "L2-normalizes a vector to unit length.",
            Category = FunctionCategory.Activation,
        });

        // ── Math — Vector Reductions ──

        RegisterVectorReduction("vec_sum", "Sum of all elements in a vector.");
        RegisterVectorReduction("vec_mean", "Arithmetic mean of all elements in a vector.");
        RegisterVectorReduction("vec_min", "Minimum element in a vector.");
        RegisterVectorReduction("vec_max", "Maximum element in a vector.");
        RegisterVectorReduction("vec_std", "Standard deviation of elements in a vector.");
        RegisterVectorReduction("vec_var", "Variance of elements in a vector.");
        RegisterVectorReduction("vec_median", "Median element of a vector.");
        RegisterVectorReduction("vec_argmin", "Index of the minimum element in a vector.");
        RegisterVectorReduction("vec_argmax", "Index of the maximum element in a vector.");
        RegisterVectorReduction("vec_norm", "L2 (Euclidean) norm of a vector.");
        RegisterVectorReduction("vec_count_nonzero", "Count of non-zero elements in a vector.");
        RegisterVectorReduction("vec_any", "Returns 1 if any element is non-zero, 0 otherwise.");
        RegisterVectorReduction("vec_all", "Returns 1 if all elements are non-zero, 0 otherwise.");
        RegisterVectorReduction("vec_product", "Product of all elements in a vector.");

        // ── Math — Tensor Introspection ──

        Register(new FunctionSignature
        {
            Name = "rank",
            Parameters = [Parameter("tensor", "Tensor")],
            ReturnType = "Float32",
            Description = "Returns the number of dimensions (rank) of a tensor.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "rdim",
            Parameters = [Parameter("tensor", "Tensor"), Parameter("dimension", "Float32")],
            ReturnType = "Float32",
            Description = "Returns the size of a specific dimension of a tensor.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "shape",
            Parameters = [Parameter("tensor", "Tensor")],
            ReturnType = "Vector",
            Description = "Returns the shape of a tensor as a vector of dimension sizes.",
            Category = FunctionCategory.Vector,
        });

        // ── Math — Vector Manipulation ──

        Register(new FunctionSignature
        {
            Name = "vec",
            Parameters = [Parameter("values", "Float32")],
            ReturnType = "Vector",
            Description = "Constructs a vector from scalar arguments: vec(1, 2, 3).",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "tensor",
            Parameters = [Parameter("values", "Float32")],
            ReturnType = "Tensor",
            Description = "Constructs a tensor from scalar arguments.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_slice",
            Parameters = [Parameter("vector", "Vector"), Parameter("start", "Float32"), Parameter("length", "Float32", isOptional: true)],
            ReturnType = "Vector",
            Description = "Extracts a contiguous slice from a vector.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_concat",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Vector",
            Description = "Concatenates two vectors into one.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_reverse",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Reverses the order of elements in a vector.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_sort",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Returns a new vector with elements sorted in ascending order.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_unique",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Returns distinct elements of a vector in original order.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_flatten",
            Parameters = [Parameter("tensor", "Tensor")],
            ReturnType = "Vector",
            Description = "Flattens a tensor of any rank into a 1-D vector.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_pad",
            Parameters = [Parameter("vector", "Vector"), Parameter("length", "Float32"), Parameter("fill", "Float32", isOptional: true)],
            ReturnType = "Vector",
            Description = "Pads a vector to the specified length with a fill value (default 0).",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "vec_repeat",
            Parameters = [Parameter("vector", "Vector"), Parameter("count", "Float32")],
            ReturnType = "Vector",
            Description = "Repeats a vector the specified number of times.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "linspace",
            Parameters = [Parameter("start", "Float32"), Parameter("stop", "Float32"), Parameter("count", "Float32")],
            ReturnType = "Vector",
            Description = "Generates a vector of evenly spaced values between start and stop (inclusive).",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "arange",
            Parameters = [Parameter("start", "Float32"), Parameter("stop", "Float32"), Parameter("step", "Float32", isOptional: true)],
            ReturnType = "Vector",
            Description = "Generates a vector of values from start to stop (exclusive) with a step (default 1).",
            Category = FunctionCategory.Vector,
        });

        // ── Math — Distance & Similarity ──

        Register(new FunctionSignature
        {
            Name = "cosine_similarity",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Float32",
            Description = "Cosine similarity between two vectors: dot(a, b) / (‖a‖ · ‖b‖).",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "euclidean_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Float32",
            Description = "Euclidean (L2) distance between two vectors.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "manhattan_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Float32",
            Description = "Manhattan (L1) distance between two vectors.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "dot",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Float32",
            Description = "Dot product (inner product) of two vectors.",
            Category = FunctionCategory.Vector,
        });
        Register(new FunctionSignature
        {
            Name = "hamming_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Float32",
            Description = "Hamming distance: count of positions where elements differ.",
            Category = FunctionCategory.Vector,
        });

        // ── Math — Utility & Conditional ──

        Register(new FunctionSignature
        {
            Name = "coalesce",
            Parameters = [Parameter("values", "Any")],
            ReturnType = null,
            Description = "Returns the first non-null argument.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "greatest",
            Parameters = [Parameter("values", "Float32")],
            ReturnType = "Float32",
            Description = "Returns the largest of the arguments.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "least",
            Parameters = [Parameter("values", "Float32")],
            ReturnType = "Float32",
            Description = "Returns the smallest of the arguments.",
            Category = FunctionCategory.Utility,
        });
        RegisterUnary("is_nan", "Returns 1 if the value is NaN, 0 otherwise.", FunctionCategory.Utility);
        RegisterUnary("is_finite", "Returns 1 if the value is finite (not NaN or infinity), 0 otherwise.", FunctionCategory.Utility);
        Register(new FunctionSignature
        {
            Name = "if_null",
            Parameters = [Parameter("value", "Any"), Parameter("default", "Any")],
            ReturnType = null,
            Description = "Returns value if non-null, otherwise returns default.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Returns a random float in [0, 1).",
            Category = FunctionCategory.Utility,
        });

        // ── Random ──

        Register(new FunctionSignature
        {
            Name = "hash_split",
            Parameters = [Parameter("key", "Any"), Parameter("seed", "Float32")],
            ReturnType = "Float32",
            Description = "Deterministic float in [0, 1) from key and seed. Enables reproducible train/val/test splits via WHERE hash_split(id, 42) < 0.8.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_int",
            Parameters = [Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Returns a random integer in [min, max] (both inclusive).",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_range",
            Parameters = [Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Returns a random float in [min, max).",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_normal",
            Parameters = [Parameter("mean", "Float32"), Parameter("stddev", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from a normal (Gaussian) distribution N(mean, stddev).",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_boolean",
            Parameters = [Parameter("probability", "Float32")],
            ReturnType = "Boolean",
            Description = "Bernoulli trial — returns true with the given probability (0 to 1).",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_truncated_normal",
            Parameters = [Parameter("mean", "Float32"), Parameter("stddev", "Float32"), Parameter("min", "Float32"), Parameter("max", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from a truncated normal distribution, clamped to [min, max].",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_log_normal",
            Parameters = [Parameter("mean", "Float32"), Parameter("stddev", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from a log-normal distribution: exp(N(mean, stddev)).",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_exponential",
            Parameters = [Parameter("rate", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from an exponential distribution with the given rate.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_beta",
            Parameters = [Parameter("alpha", "Float32"), Parameter("beta", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from a Beta(alpha, beta) distribution.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_poisson",
            Parameters = [Parameter("lambda", "Float32")],
            ReturnType = "Float32",
            Description = "Samples from a Poisson distribution with the given rate.",
            Category = FunctionCategory.Utility,
        });
        Register(new FunctionSignature
        {
            Name = "random_categorical",
            Parameters = [Parameter("weights", "Vector")],
            ReturnType = "Float32",
            Description = "Draws a 0-based category index from weighted probabilities.",
            Category = FunctionCategory.Utility,
            QueryUnitCost = 2,
        });
        Register(new FunctionSignature
        {
            Name = "random_vector",
            Parameters = [Parameter("length", "Float32")],
            ReturnType = "Vector",
            Description = "Generates a vector of uniform random floats in [0, 1).",
            Category = FunctionCategory.Utility,
            QueryUnitCost = 2,
        });
        Register(new FunctionSignature
        {
            Name = "random_normal_vector",
            Parameters = [Parameter("length", "Float32"), Parameter("mean", "Float32"), Parameter("stddev", "Float32")],
            ReturnType = "Vector",
            Description = "Generates a vector of Gaussian random floats N(mean, stddev).",
            Category = FunctionCategory.Utility,
            QueryUnitCost = 2,
        });
        Register(new FunctionSignature
        {
            Name = "random_permutation",
            Parameters = [Parameter("length", "Float32")],
            ReturnType = "Vector",
            Description = "Generates a random permutation of [0, length).",
            Category = FunctionCategory.Utility,
            QueryUnitCost = 2,
        });
        Register(new FunctionSignature
        {
            Name = "random_choice",
            Parameters = [Parameter("array", "Array"), Parameter("count", "Float32")],
            ReturnType = "Array",
            Description = "Samples count elements from an array without replacement.",
            Category = FunctionCategory.Utility,
            QueryUnitCost = 2,
        });

        // ── Image — Metadata ──

        RegisterImageUnary("width", "Float32", "Returns the width (in pixels) of an image.");
        RegisterImageUnary("height", "Float32", "Returns the height (in pixels) of an image.");
        RegisterImageUnary("channels", "Float32", "Returns the number of color channels in an image.");
        RegisterImageUnary("pixel_count", "Float32", "Returns the total pixel count (width × height) of an image.");
        RegisterImageUnary("dimensions", "Vector", "Returns [width, height, channels] as a vector.");

        // ── Image — Loading & Decode ──

        Register(new FunctionSignature
        {
            Name = "load_image",
            Parameters = [Parameter("path", "String")],
            ReturnType = "Image",
            Description = "Loads an image from a file path.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "image_to_bytes",
            Parameters = [Parameter("img", "Image")],
            ReturnType = "UInt8Array",
            Description = "Extracts raw RGBA pixel bytes from an image.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "image_to_tensor_hwc",
            Parameters = [Parameter("img", "Image")],
            ReturnType = "Tensor",
            Description = "Decodes image to [H, W, 3] RGB float tensor (HWC layout).",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "image_to_tensor_chw",
            Parameters = [Parameter("img", "Image")],
            ReturnType = "Tensor",
            Description = "Decodes image to [3, H, W] RGB float tensor (CHW layout).",
            Category = FunctionCategory.Image,
        });

        // ── Image — Analysis ──

        RegisterImageUnary("brightness_mean", "Float32", "Mean brightness (luminance) across all pixels.");
        RegisterImageUnary("brightness_std", "Float32", "Standard deviation of brightness across all pixels.");
        RegisterImageUnary("brightness_histogram", "Vector", "256-bin histogram of brightness values.");
        RegisterImageUnary("detect_blur", "Float32", "Laplacian variance blur detection score (lower = blurrier).");
        RegisterImageUnary("compression_artifact_score", "Float32", "Estimates JPEG compression artifact severity.");

        // ── Image — Pixel Statistics ──

        RegisterImageUnary("pixel_mean", "Vector", "Per-channel mean pixel values.");
        RegisterImageUnary("pixel_std", "Vector", "Per-channel pixel standard deviations.");

        // ── Image — Transforms ──

        Register(new FunctionSignature
        {
            Name = "resize",
            Parameters = [Parameter("image", "Image"), Parameter("width", "Float32"), Parameter("height", "Float32")],
            ReturnType = "Image",
            Description = "Resizes an image to the specified width and height.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "crop",
            Parameters = [Parameter("image", "Image"), Parameter("x", "Float32"), Parameter("y", "Float32"), Parameter("width", "Float32"), Parameter("height", "Float32")],
            ReturnType = "Image",
            Description = "Crops a rectangular region from an image.",
            Category = FunctionCategory.Image,
        });
        RegisterImageTransform("grayscale", "Converts an image to grayscale.");
        Register(new FunctionSignature
        {
            Name = "rotate",
            Parameters = [Parameter("image", "Image"), Parameter("degrees", "Float32")],
            ReturnType = "Image",
            Description = "Rotates an image by the specified angle in degrees.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "noise",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Float32")],
            ReturnType = "Image",
            Description = "Adds random noise to an image.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "blur",
            Parameters = [Parameter("image", "Image"), Parameter("radius", "Float32")],
            ReturnType = "Image",
            Description = "Applies Gaussian blur with the specified radius.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "brighten",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Float32")],
            ReturnType = "Image",
            Description = "Increases image brightness by the specified amount.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "darken",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Float32")],
            ReturnType = "Image",
            Description = "Decreases image brightness by the specified amount.",
            Category = FunctionCategory.Image,
        });
        RegisterImageTransform("sobel", "Applies Sobel edge detection filter.");
        Register(new FunctionSignature
        {
            Name = "resize_and_crop",
            Parameters = [Parameter("image", "Image"), Parameter("width", "Float32"), Parameter("height", "Float32")],
            ReturnType = "Image",
            Description = "Resizes preserving aspect ratio, then center-crops to target dimensions.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "affine_transform",
            Parameters = [Parameter("image", "Image"), Parameter("matrix", "Vector")],
            ReturnType = "Image",
            Description = "Applies a 2×3 affine transformation matrix to an image.",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "elastic_deform",
            Parameters = [Parameter("image", "Image"), Parameter("alpha", "Float32"), Parameter("sigma", "Float32")],
            ReturnType = "Image",
            Description = "Applies elastic deformation with specified intensity (alpha) and smoothness (sigma).",
            Category = FunctionCategory.Image,
        });
        Register(new FunctionSignature
        {
            Name = "perspective_warp",
            Parameters = [Parameter("image", "Image"), Parameter("strength", "Float32")],
            ReturnType = "Image",
            Description = "Applies a random perspective warp transformation.",
            Category = FunctionCategory.Image,
        });

        // ── Image — Hashing ──

        RegisterImageUnary("perceptual_hash", "UInt8Array", "Computes a perceptual hash for image similarity comparison.");

        // ── Hashing ──

        Register(new FunctionSignature
        {
            Name = "md5",
            Parameters = [Parameter("input", "String")],
            ReturnType = "UInt8Array",
            Description = "Computes the MD5 hash of the input and returns the raw hash bytes.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "sha256",
            Parameters = [Parameter("input", "String")],
            ReturnType = "UInt8Array",
            Description = "Computes the SHA-256 hash of the input and returns the raw hash bytes.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "sha512",
            Parameters = [Parameter("input", "String")],
            ReturnType = "UInt8Array",
            Description = "Computes the SHA-512 hash of the input and returns the raw hash bytes.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "crc32",
            Parameters = [Parameter("input", "String")],
            ReturnType = "Float32",
            Description = "Computes the CRC-32 checksum of the input.",
            Category = FunctionCategory.Encoding,
        });

        // ── Encoding ──

        Register(new FunctionSignature
        {
            Name = "base64_encode",
            Parameters = [Parameter("input", "UInt8Array")],
            ReturnType = "String",
            Description = "Encodes a byte array as a Base64 string.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "base64_decode",
            Parameters = [Parameter("input", "String")],
            ReturnType = "UInt8Array",
            Description = "Decodes a Base64-encoded string into a byte array.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "hex_encode",
            Parameters = [Parameter("input", "UInt8Array")],
            ReturnType = "String",
            Description = "Encodes a byte array as a lowercase hexadecimal string.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "hex_decode",
            Parameters = [Parameter("input", "String")],
            ReturnType = "UInt8Array",
            Description = "Decodes a hexadecimal string into a byte array.",
            Category = FunctionCategory.Encoding,
        });

        // ── UUID ──

        Register(new FunctionSignature
        {
            Name = "uuid4",
            Parameters = [],
            ReturnType = "Uuid",
            Description = "Generates a random version-4 UUID (RFC 9562).",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "uuid7",
            Parameters = [],
            ReturnType = "Uuid",
            Description = "Generates a time-ordered version-7 UUID (RFC 9562) with an embedded millisecond timestamp.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "is_uuid",
            Parameters = [Parameter("input", "String")],
            ReturnType = "Boolean",
            Description = "Tests whether a string is a valid UUID.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "uuid_str",
            Parameters = [Parameter("input", "Uuid")],
            ReturnType = "String",
            Description = "Formats a UUID as a lowercase hyphenated string.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "uuid_bytes",
            Parameters = [Parameter("input", "Uuid")],
            ReturnType = "UInt8Array",
            Description = "Extracts the raw bytes of a UUID as a 16-element big-endian byte array.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "uuid_version",
            Parameters = [Parameter("input", "Uuid")],
            ReturnType = "Float32",
            Description = "Extracts the version number from a UUID.",
            Category = FunctionCategory.Encoding,
        });
        Register(new FunctionSignature
        {
            Name = "uuid_timestamp",
            Parameters = [Parameter("input", "Uuid")],
            ReturnType = "DateTime",
            Description = "Extracts the embedded timestamp from a version-7 UUID; returns null for non-v7 UUIDs.",
            Category = FunctionCategory.Encoding,
        });

        // ── Table-Valued Functions ──

        Register(new FunctionSignature
        {
            Name = "unnest",
            Parameters = [Parameter("array_column", "Vector")],
            ReturnType = "Float32",
            Description = "Expands a vector column into individual rows with a Value column.",
            IsTableValued = true,
            Category = FunctionCategory.Table,
        });
        Register(new FunctionSignature
        {
            Name = "range",
            Parameters = [Parameter("start", "Float32"), Parameter("stop", "Float32"), Parameter("step", "Float32", isOptional: true)],
            ReturnType = "Float32",
            Description = "Generates rows with a Value column from start to stop (inclusive) with an optional step.",
            IsTableValued = true,
            Category = FunctionCategory.Table,
        });

        // ── Aggregate Functions ──

        Register(new FunctionSignature
        {
            Name = "COUNT",
            Parameters = [Parameter("expression", "Any", isOptional: true)],
            ReturnType = "Float32",
            Description = "Counts the number of rows. COUNT(*) counts all rows; COUNT(expr) counts non-null values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "SUM",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Returns the sum of all non-null values in the group.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "AVG",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Returns the arithmetic mean of all non-null values in the group.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "MIN",
            Parameters = [Parameter("expression", "Any")],
            ReturnType = "Any",
            Description = "Returns the minimum value in the group. Works on numeric, string, date, and time types.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "MAX",
            Parameters = [Parameter("expression", "Any")],
            ReturnType = "Any",
            Description = "Returns the maximum value in the group. Works on numeric, string, date, and time types.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "VARIANCE",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Sample variance (N\u22121 denominator) of non-null values. Alias for VAR_SAMP.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "VAR_SAMP",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Sample variance (N\u22121 denominator) of non-null values. Returns null for fewer than 2 values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "VAR_POP",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Population variance (N denominator) of non-null values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "STDDEV",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Sample standard deviation (N\u22121 denominator) of non-null values. Alias for STDDEV_SAMP.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "STDDEV_SAMP",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Sample standard deviation (N\u22121 denominator) of non-null values. Returns null for fewer than 2 values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "STDDEV_POP",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Population standard deviation (N denominator) of non-null values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "MEDIAN",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Median (50th percentile) of non-null values. For even counts, returns the average of the two middle values.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "PERCENTILE_CONT",
            Parameters = [Parameter("expression", "Float32"), Parameter("fraction", "Float32")],
            ReturnType = "Float32",
            Description = "Continuous percentile using linear interpolation. Fraction must be between 0 and 1.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "PERCENTILE_DISC",
            Parameters = [Parameter("expression", "Float32"), Parameter("fraction", "Float32")],
            ReturnType = "Float32",
            Description = "Discrete percentile (nearest rank). Returns an actually observed value. Fraction in [0, 1].",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "MODE",
            Parameters = [Parameter("expression", "Any")],
            ReturnType = "Any",
            Description = "Returns the most frequently occurring value. Ties broken by first occurrence.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "CORR",
            Parameters = [Parameter("y", "Float32"), Parameter("x", "Float32")],
            ReturnType = "Float32",
            Description = "Pearson correlation coefficient between two numeric columns. Returns value in [−1, 1].",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "COVAR_POP",
            Parameters = [Parameter("y", "Float32"), Parameter("x", "Float32")],
            ReturnType = "Float32",
            Description = "Population covariance (N denominator) between two numeric columns.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "COVAR_SAMP",
            Parameters = [Parameter("y", "Float32"), Parameter("x", "Float32")],
            ReturnType = "Float32",
            Description = "Sample covariance (N−1 denominator) between two numeric columns. Null for fewer than 2 pairs.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "APPROX_MEDIAN",
            Parameters = [Parameter("expression", "Float32")],
            ReturnType = "Float32",
            Description = "Approximate median using reservoir sampling. O(1) memory, ~1–5% error for large groups.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "APPROX_PERCENTILE",
            Parameters = [Parameter("expression", "Float32"), Parameter("fraction", "Float32")],
            ReturnType = "Float32",
            Description = "Approximate percentile using reservoir sampling. O(1) memory, ~1–5% error for large groups.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });
        Register(new FunctionSignature
        {
            Name = "STRING_AGG",
            Parameters = [Parameter("expression", "String"), Parameter("separator", "String")],
            ReturnType = "String",
            Description = "Concatenates non-null string values with a separator. Supports ORDER BY inside the function call.",
            IsAggregate = true,
            Category = FunctionCategory.Aggregate,
        });

        RegisterWindowFunctions();
    }

    private static void RegisterWindowFunctions()
    {
        Register(new FunctionSignature
        {
            Name = "ROW_NUMBER",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Assigns a unique sequential integer to each row within its partition, starting at 1.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "RANK",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Assigns a rank to each row within its partition based on ORDER BY values. Ties receive the same rank with gaps after.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "DENSE_RANK",
            Parameters = [],
            ReturnType = "Float32",
            Description = "Assigns a rank to each row within its partition based on ORDER BY values. Ties receive the same rank without gaps.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "NTILE",
            Parameters = [Parameter("buckets", "Float32")],
            ReturnType = "Float32",
            Description = "Distributes rows of an ordered partition into the specified number of approximately equal-sized buckets.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "LAG",
            Parameters = [Parameter("expression", "Any"), Parameter("offset", "Float32", isOptional: true), Parameter("default", "Any", isOptional: true)],
            ReturnType = "Any",
            Description = "Returns the value of the expression from a preceding row within the partition. Default offset is 1.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "LEAD",
            Parameters = [Parameter("expression", "Any"), Parameter("offset", "Float32", isOptional: true), Parameter("default", "Any", isOptional: true)],
            ReturnType = "Any",
            Description = "Returns the value of the expression from a following row within the partition. Default offset is 1.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "FIRST_VALUE",
            Parameters = [Parameter("expression", "Any")],
            ReturnType = "Any",
            Description = "Returns the value of the expression from the first row in the window frame. Supports IGNORE NULLS to skip null values.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "LAST_VALUE",
            Parameters = [Parameter("expression", "Any")],
            ReturnType = "Any",
            Description = "Returns the value of the expression from the last row in the window frame. Supports IGNORE NULLS to skip null values.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
        Register(new FunctionSignature
        {
            Name = "NTH_VALUE",
            Parameters = [Parameter("expression", "Any"), Parameter("n", "Float32")],
            ReturnType = "Any",
            Description = "Returns the value of the expression from the Nth row (1-based) in the window frame. Supports FROM FIRST/LAST and IGNORE NULLS.",
            IsWindowFunction = true,
            Category = FunctionCategory.Window,
        });
    }

    /// <summary>Registers a standard unary numeric function (operates element-wise on Scalar/Vector/Tensor).</summary>
    private static void RegisterUnary(string name, string description, FunctionCategory category = FunctionCategory.Numeric)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("value", "Float32")],
            ReturnType = "Float32",
            Description = description,
            Category = category,
        });
    }

    /// <summary>Registers a standard binary numeric function.</summary>
    private static void RegisterBinary(string name, string leftName, string rightName, string description, FunctionCategory category = FunctionCategory.Numeric)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter(leftName, "Float32"), Parameter(rightName, "Float32")],
            ReturnType = "Float32",
            Description = description,
            Category = category,
        });
    }

    /// <summary>Registers a vector-to-scalar reduction function.</summary>
    private static void RegisterVectorReduction(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Float32",
            Description = description,
            Category = FunctionCategory.Vector,
        });
    }

    /// <summary>Registers a unary image analysis function (image → result).</summary>
    private static void RegisterImageUnary(string name, string returnType, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("image", "Image")],
            ReturnType = returnType,
            Description = description,
            Category = FunctionCategory.Image,
        });
    }

    /// <summary>Registers a unary image transform (image → image).</summary>
    private static void RegisterImageTransform(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("image", "Image")],
            ReturnType = "Image",
            Description = description,
            Category = FunctionCategory.Image,
        });
    }

    /// <summary>Registers a date/time extraction function (Date/DateTime → Scalar).</summary>
    private static void RegisterDateExtraction(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("date", "DateTime")],
            ReturnType = "Float32",
            Description = description,
            Category = FunctionCategory.Temporal,
        });
    }
}
