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
            Parameters = [Parameter("value", "Scalar"), Parameter("min", "Scalar"), Parameter("max", "Scalar")],
            ReturnType = "Scalar",
            Description = "Normalizes a value to [0, 1] given a known min/max range."
        });
        Register(new FunctionSignature
        {
            Name = "clamp",
            Parameters = [Parameter("value", "Scalar"), Parameter("min", "Scalar"), Parameter("max", "Scalar")],
            ReturnType = "Scalar",
            Description = "Clamps a value to the range [min, max]."
        });
        Register(new FunctionSignature
        {
            Name = "denormalize",
            Parameters = [Parameter("value", "Scalar"), Parameter("min", "Scalar"), Parameter("max", "Scalar")],
            ReturnType = "Scalar",
            Description = "Maps a [0, 1] value back to the original [min, max] range."
        });
        Register(new FunctionSignature
        {
            Name = "reshape",
            Parameters = [Parameter("tensor", "Tensor"), Parameter("dim1", "Scalar"), Parameter("dim2", "Scalar", isOptional: true)],
            ReturnType = "Tensor",
            Description = "Reinterprets the shape of a tensor without copying data. Element count must match."
        });

        // ── String ──

        Register(new FunctionSignature
        {
            Name = "len",
            Parameters = [Parameter("value", "String")],
            ReturnType = "Scalar",
            Description = "Returns the character length of a string."
        });
        Register(new FunctionSignature
        {
            Name = "mid",
            Parameters = [Parameter("value", "String"), Parameter("start", "Scalar"), Parameter("length", "Scalar")],
            ReturnType = "String",
            Description = "Extracts a substring starting at the given 1-based position with the specified length."
        });
        Register(new FunctionSignature
        {
            Name = "substring",
            Parameters = [Parameter("value", "String"), Parameter("start", "Scalar"), Parameter("length", "Scalar", isOptional: true)],
            ReturnType = "String",
            Description = "Extracts a substring from a 0-based start position, optionally with a length."
        });
        Register(new FunctionSignature
        {
            Name = "get_filename",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the file name (with extension) from a file path."
        });
        Register(new FunctionSignature
        {
            Name = "get_file_extension",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the file extension (including the dot) from a file path."
        });
        Register(new FunctionSignature
        {
            Name = "get_path",
            Parameters = [Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts the directory path from a file path."
        });

        // ── Type Conversion ──

        Register(new FunctionSignature
        {
            Name = "cast",
            Parameters = [Parameter("value", "Any"), Parameter("target_type", "String")],
            ReturnType = null,
            Description = "Explicit type conversion between DataKind types. Target type is a DataKind name."
        });
        Register(new FunctionSignature
        {
            Name = "to_epoch",
            Parameters = [Parameter("value", "DateTime")],
            ReturnType = "Scalar",
            Description = "Converts a Date or DateTime to epoch seconds (float)."
        });
        Register(new FunctionSignature
        {
            Name = "date_part",
            Parameters = [Parameter("part", "String"), Parameter("value", "DateTime")],
            ReturnType = "Scalar",
            Description = "Extracts a component (year, month, day, hour, minute, second) from a Date or DateTime."
        });
        Register(new FunctionSignature
        {
            Name = "cyclical_encode",
            Parameters = [Parameter("value", "Scalar"), Parameter("period", "Scalar")],
            ReturnType = "Vector",
            Description = "Encodes a cyclic value as a [sin, cos] pair for ML features."
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
            Description = "Returns the current UTC timestamp."
        });
        Register(new FunctionSignature
        {
            Name = "make_date",
            Parameters = [Parameter("year", "Scalar"), Parameter("month", "Scalar"), Parameter("day", "Scalar")],
            ReturnType = "Date",
            Description = "Constructs a Date from year, month, and day components."
        });
        Register(new FunctionSignature
        {
            Name = "make_timestamp",
            Parameters = [Parameter("year", "Scalar"), Parameter("month", "Scalar"), Parameter("day", "Scalar"), Parameter("hour", "Scalar"), Parameter("minute", "Scalar"), Parameter("second", "Scalar")],
            ReturnType = "DateTime",
            Description = "Constructs a UTC DateTime from year, month, day, hour, minute, and second components."
        });
        Register(new FunctionSignature
        {
            Name = "date_diff",
            Parameters = [Parameter("part", "String"), Parameter("start", "DateTime"), Parameter("end", "DateTime")],
            ReturnType = "Scalar",
            Description = "Returns the number of date part boundaries between start and end."
        });
        Register(new FunctionSignature
        {
            Name = "date_add",
            Parameters = [Parameter("part", "String"), Parameter("number", "Scalar"), Parameter("date", "DateTime")],
            ReturnType = "DateTime",
            Description = "Adds the specified number of date part units to a date."
        });
        Register(new FunctionSignature
        {
            Name = "date_trunc",
            Parameters = [Parameter("part", "String"), Parameter("date", "DateTime")],
            ReturnType = "DateTime",
            Description = "Truncates a date to the specified precision (e.g., month → first of month)."
        });
        Register(new FunctionSignature
        {
            Name = "date_bucket",
            Parameters = [Parameter("part", "String"), Parameter("width", "Scalar"), Parameter("date", "DateTime"), Parameter("origin", "DateTime", isOptional: true)],
            ReturnType = "DateTime",
            Description = "Buckets a date into fixed-width intervals of the specified date part."
        });

        // ── Date/Time — Formatting & Probing ──

        Register(new FunctionSignature
        {
            Name = "strftime",
            Parameters = [Parameter("date", "DateTime"), Parameter("format", "String")],
            ReturnType = "String",
            Description = "Formats a Date or DateTime as a string using a .NET format string."
        });
        Register(new FunctionSignature
        {
            Name = "is_date",
            Parameters = [Parameter("value", "String")],
            ReturnType = "Scalar",
            Description = "Returns 1 if the string can be parsed as a date, 0 otherwise."
        });

        // ── JSON ──

        Register(new FunctionSignature
        {
            Name = "json_value",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "String",
            Description = "Extracts a scalar value from a JSON document at the specified path."
        });
        Register(new FunctionSignature
        {
            Name = "json_query",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "JsonValue",
            Description = "Extracts a JSON object or array from a JSON document at the specified path."
        });
        Register(new FunctionSignature
        {
            Name = "json_exists",
            Parameters = [Parameter("json", "JsonValue"), Parameter("path", "String")],
            ReturnType = "Scalar",
            Description = "Returns 1 if the path exists in the JSON document, 0 otherwise."
        });
        Register(new FunctionSignature
        {
            Name = "json_array_length",
            Parameters = [Parameter("json", "JsonValue")],
            ReturnType = "Scalar",
            Description = "Returns the number of elements in a JSON array."
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
            ReturnType = "Scalar",
            Description = "Returns the constant π (3.14159...)."
        });
        Register(new FunctionSignature
        {
            Name = "euler",
            Parameters = [],
            ReturnType = "Scalar",
            Description = "Returns Euler's number e (2.71828...)."
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
            Parameters = [Parameter("value", "Scalar"), Parameter("min", "Scalar"), Parameter("max", "Scalar")],
            ReturnType = "Scalar",
            Description = "Clips a value to the range [min, max]. Alias for clamp."
        });

        // ── Math — ML Activations ──

        RegisterUnary("sigmoid", "Sigmoid activation: σ(x) = 1 / (1 + e⁻ˣ).");
        RegisterUnary("relu", "Rectified linear unit: relu(x) = max(0, x).");
        RegisterUnary("selu", "Scaled exponential linear unit.");
        RegisterUnary("gelu", "Gaussian error linear unit.");
        RegisterUnary("swish", "Swish activation: swish(x) = x · σ(x).");
        RegisterUnary("softplus", "Softplus: softplus(x) = ln(1 + eˣ).");
        RegisterUnary("softsign", "Softsign: softsign(x) = x / (1 + |x|).");
        RegisterUnary("mish", "Mish activation: mish(x) = x · tanh(softplus(x)).");
        RegisterUnary("hard_sigmoid", "Piecewise-linear approximation of sigmoid.");
        RegisterUnary("hard_swish", "Piecewise-linear approximation of swish.");
        Register(new FunctionSignature
        {
            Name = "leaky_relu",
            Parameters = [Parameter("value", "Scalar"), Parameter("alpha", "Scalar", isOptional: true)],
            ReturnType = "Scalar",
            Description = "Leaky ReLU with configurable negative slope (default α = 0.01)."
        });
        Register(new FunctionSignature
        {
            Name = "elu",
            Parameters = [Parameter("value", "Scalar"), Parameter("alpha", "Scalar", isOptional: true)],
            ReturnType = "Scalar",
            Description = "Exponential linear unit with configurable α (default α = 1.0)."
        });

        // ── Math — Softmax & Normalization ──

        Register(new FunctionSignature
        {
            Name = "softmax",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Applies softmax normalization: each element becomes exp(xᵢ) / Σexp(xⱼ)."
        });
        Register(new FunctionSignature
        {
            Name = "log_softmax",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Log of softmax: numerically stable version of ln(softmax(x))."
        });
        Register(new FunctionSignature
        {
            Name = "l2_normalize",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "L2-normalizes a vector to unit length."
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
            ReturnType = "Scalar",
            Description = "Returns the number of dimensions (rank) of a tensor."
        });
        Register(new FunctionSignature
        {
            Name = "rdim",
            Parameters = [Parameter("tensor", "Tensor"), Parameter("dimension", "Scalar")],
            ReturnType = "Scalar",
            Description = "Returns the size of a specific dimension of a tensor."
        });
        Register(new FunctionSignature
        {
            Name = "shape",
            Parameters = [Parameter("tensor", "Tensor")],
            ReturnType = "Vector",
            Description = "Returns the shape of a tensor as a vector of dimension sizes."
        });

        // ── Math — Vector Manipulation ──

        Register(new FunctionSignature
        {
            Name = "vec",
            Parameters = [Parameter("values", "Scalar")],
            ReturnType = "Vector",
            Description = "Constructs a vector from scalar arguments: vec(1, 2, 3)."
        });
        Register(new FunctionSignature
        {
            Name = "tensor",
            Parameters = [Parameter("values", "Scalar")],
            ReturnType = "Tensor",
            Description = "Constructs a tensor from scalar arguments."
        });
        Register(new FunctionSignature
        {
            Name = "vec_slice",
            Parameters = [Parameter("vector", "Vector"), Parameter("start", "Scalar"), Parameter("length", "Scalar", isOptional: true)],
            ReturnType = "Vector",
            Description = "Extracts a contiguous slice from a vector."
        });
        Register(new FunctionSignature
        {
            Name = "vec_concat",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Vector",
            Description = "Concatenates two vectors into one."
        });
        Register(new FunctionSignature
        {
            Name = "vec_reverse",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Reverses the order of elements in a vector."
        });
        Register(new FunctionSignature
        {
            Name = "vec_sort",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Returns a new vector with elements sorted in ascending order."
        });
        Register(new FunctionSignature
        {
            Name = "vec_unique",
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Vector",
            Description = "Returns distinct elements of a vector in original order."
        });
        Register(new FunctionSignature
        {
            Name = "vec_flatten",
            Parameters = [Parameter("tensor", "Tensor")],
            ReturnType = "Vector",
            Description = "Flattens a tensor of any rank into a 1-D vector."
        });
        Register(new FunctionSignature
        {
            Name = "vec_pad",
            Parameters = [Parameter("vector", "Vector"), Parameter("length", "Scalar"), Parameter("fill", "Scalar", isOptional: true)],
            ReturnType = "Vector",
            Description = "Pads a vector to the specified length with a fill value (default 0)."
        });
        Register(new FunctionSignature
        {
            Name = "vec_repeat",
            Parameters = [Parameter("vector", "Vector"), Parameter("count", "Scalar")],
            ReturnType = "Vector",
            Description = "Repeats a vector the specified number of times."
        });
        Register(new FunctionSignature
        {
            Name = "linspace",
            Parameters = [Parameter("start", "Scalar"), Parameter("stop", "Scalar"), Parameter("count", "Scalar")],
            ReturnType = "Vector",
            Description = "Generates a vector of evenly spaced values between start and stop (inclusive)."
        });
        Register(new FunctionSignature
        {
            Name = "arange",
            Parameters = [Parameter("start", "Scalar"), Parameter("stop", "Scalar"), Parameter("step", "Scalar", isOptional: true)],
            ReturnType = "Vector",
            Description = "Generates a vector of values from start to stop (exclusive) with a step (default 1)."
        });

        // ── Math — Distance & Similarity ──

        Register(new FunctionSignature
        {
            Name = "cosine_similarity",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Scalar",
            Description = "Cosine similarity between two vectors: dot(a, b) / (‖a‖ · ‖b‖)."
        });
        Register(new FunctionSignature
        {
            Name = "euclidean_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Scalar",
            Description = "Euclidean (L2) distance between two vectors."
        });
        Register(new FunctionSignature
        {
            Name = "manhattan_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Scalar",
            Description = "Manhattan (L1) distance between two vectors."
        });
        Register(new FunctionSignature
        {
            Name = "dot",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Scalar",
            Description = "Dot product (inner product) of two vectors."
        });
        Register(new FunctionSignature
        {
            Name = "hamming_distance",
            Parameters = [Parameter("left", "Vector"), Parameter("right", "Vector")],
            ReturnType = "Scalar",
            Description = "Hamming distance: count of positions where elements differ."
        });

        // ── Math — Utility & Conditional ──

        Register(new FunctionSignature
        {
            Name = "coalesce",
            Parameters = [Parameter("values", "Any")],
            ReturnType = null,
            Description = "Returns the first non-null argument."
        });
        Register(new FunctionSignature
        {
            Name = "greatest",
            Parameters = [Parameter("values", "Scalar")],
            ReturnType = "Scalar",
            Description = "Returns the largest of the arguments."
        });
        Register(new FunctionSignature
        {
            Name = "least",
            Parameters = [Parameter("values", "Scalar")],
            ReturnType = "Scalar",
            Description = "Returns the smallest of the arguments."
        });
        RegisterUnary("is_nan", "Returns 1 if the value is NaN, 0 otherwise.");
        RegisterUnary("is_finite", "Returns 1 if the value is finite (not NaN or infinity), 0 otherwise.");
        Register(new FunctionSignature
        {
            Name = "if_null",
            Parameters = [Parameter("value", "Any"), Parameter("default", "Any")],
            ReturnType = null,
            Description = "Returns value if non-null, otherwise returns default."
        });
        Register(new FunctionSignature
        {
            Name = "random",
            Parameters = [],
            ReturnType = "Scalar",
            Description = "Returns a random float in [0, 1)."
        });

        // ── Image — Metadata ──

        RegisterImageUnary("width", "Scalar", "Returns the width (in pixels) of an image.");
        RegisterImageUnary("height", "Scalar", "Returns the height (in pixels) of an image.");
        RegisterImageUnary("channels", "Scalar", "Returns the number of color channels in an image.");
        RegisterImageUnary("pixel_count", "Scalar", "Returns the total pixel count (width × height) of an image.");
        RegisterImageUnary("dimensions", "Vector", "Returns [width, height, channels] as a vector.");

        // ── Image — Loading & Decode ──

        Register(new FunctionSignature
        {
            Name = "load_image",
            Parameters = [Parameter("path", "String")],
            ReturnType = "Image",
            Description = "Loads an image from a file path."
        });
        Register(new FunctionSignature
        {
            Name = "decode_image",
            Parameters = [Parameter("bytes", "UInt8Array")],
            ReturnType = "Image",
            Description = "Decodes an image from raw bytes."
        });

        // ── Image — Analysis ──

        RegisterImageUnary("brightness_mean", "Scalar", "Mean brightness (luminance) across all pixels.");
        RegisterImageUnary("brightness_std", "Scalar", "Standard deviation of brightness across all pixels.");
        RegisterImageUnary("brightness_histogram", "Vector", "256-bin histogram of brightness values.");
        RegisterImageUnary("detect_blur", "Scalar", "Laplacian variance blur detection score (lower = blurrier).");
        RegisterImageUnary("compression_artifact_score", "Scalar", "Estimates JPEG compression artifact severity.");

        // ── Image — Pixel Statistics ──

        RegisterImageUnary("pixel_mean", "Vector", "Per-channel mean pixel values.");
        RegisterImageUnary("pixel_std", "Vector", "Per-channel pixel standard deviations.");

        // ── Image — Transforms ──

        Register(new FunctionSignature
        {
            Name = "resize",
            Parameters = [Parameter("image", "Image"), Parameter("width", "Scalar"), Parameter("height", "Scalar")],
            ReturnType = "Image",
            Description = "Resizes an image to the specified width and height."
        });
        Register(new FunctionSignature
        {
            Name = "crop",
            Parameters = [Parameter("image", "Image"), Parameter("x", "Scalar"), Parameter("y", "Scalar"), Parameter("width", "Scalar"), Parameter("height", "Scalar")],
            ReturnType = "Image",
            Description = "Crops a rectangular region from an image."
        });
        RegisterImageTransform("grayscale", "Converts an image to grayscale.");
        Register(new FunctionSignature
        {
            Name = "rotate",
            Parameters = [Parameter("image", "Image"), Parameter("degrees", "Scalar")],
            ReturnType = "Image",
            Description = "Rotates an image by the specified angle in degrees."
        });
        Register(new FunctionSignature
        {
            Name = "noise",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Scalar")],
            ReturnType = "Image",
            Description = "Adds random noise to an image."
        });
        Register(new FunctionSignature
        {
            Name = "blur",
            Parameters = [Parameter("image", "Image"), Parameter("radius", "Scalar")],
            ReturnType = "Image",
            Description = "Applies Gaussian blur with the specified radius."
        });
        Register(new FunctionSignature
        {
            Name = "brighten",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Scalar")],
            ReturnType = "Image",
            Description = "Increases image brightness by the specified amount."
        });
        Register(new FunctionSignature
        {
            Name = "darken",
            Parameters = [Parameter("image", "Image"), Parameter("amount", "Scalar")],
            ReturnType = "Image",
            Description = "Decreases image brightness by the specified amount."
        });
        RegisterImageTransform("sobel", "Applies Sobel edge detection filter.");
        Register(new FunctionSignature
        {
            Name = "resize_and_crop",
            Parameters = [Parameter("image", "Image"), Parameter("width", "Scalar"), Parameter("height", "Scalar")],
            ReturnType = "Image",
            Description = "Resizes preserving aspect ratio, then center-crops to target dimensions."
        });
        Register(new FunctionSignature
        {
            Name = "affine_transform",
            Parameters = [Parameter("image", "Image"), Parameter("matrix", "Vector")],
            ReturnType = "Image",
            Description = "Applies a 2×3 affine transformation matrix to an image."
        });
        Register(new FunctionSignature
        {
            Name = "elastic_deform",
            Parameters = [Parameter("image", "Image"), Parameter("alpha", "Scalar"), Parameter("sigma", "Scalar")],
            ReturnType = "Image",
            Description = "Applies elastic deformation with specified intensity (alpha) and smoothness (sigma)."
        });
        Register(new FunctionSignature
        {
            Name = "perspective_warp",
            Parameters = [Parameter("image", "Image"), Parameter("strength", "Scalar")],
            ReturnType = "Image",
            Description = "Applies a random perspective warp transformation."
        });

        // ── Image — Hashing ──

        RegisterImageUnary("perceptual_hash", "UInt8Array", "Computes a perceptual hash for image similarity comparison.");

        // ── Table-Valued Functions ──

        Register(new FunctionSignature
        {
            Name = "unnest",
            Parameters = [Parameter("array_column", "Vector")],
            ReturnType = "Scalar",
            Description = "Expands a vector column into individual rows with a Value column.",
            IsTableValued = true
        });
        Register(new FunctionSignature
        {
            Name = "range",
            Parameters = [Parameter("start", "Scalar"), Parameter("stop", "Scalar"), Parameter("step", "Scalar", isOptional: true)],
            ReturnType = "Scalar",
            Description = "Generates rows with a Value column from start to stop (inclusive) with an optional step.",
            IsTableValued = true
        });
    }

    /// <summary>Registers a standard unary numeric function (operates element-wise on Scalar/Vector/Tensor).</summary>
    private static void RegisterUnary(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("value", "Scalar")],
            ReturnType = "Scalar",
            Description = description
        });
    }

    /// <summary>Registers a standard binary numeric function.</summary>
    private static void RegisterBinary(string name, string leftName, string rightName, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter(leftName, "Scalar"), Parameter(rightName, "Scalar")],
            ReturnType = "Scalar",
            Description = description
        });
    }

    /// <summary>Registers a vector-to-scalar reduction function.</summary>
    private static void RegisterVectorReduction(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("vector", "Vector")],
            ReturnType = "Scalar",
            Description = description
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
            Description = description
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
            Description = description
        });
    }

    /// <summary>Registers a date/time extraction function (Date/DateTime → Scalar).</summary>
    private static void RegisterDateExtraction(string name, string description)
    {
        Register(new FunctionSignature
        {
            Name = name,
            Parameters = [Parameter("date", "DateTime")],
            ReturnType = "Scalar",
            Description = description
        });
    }
}
