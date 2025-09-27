namespace DatumIngest.Editor;

/// <summary>
/// Builds the Monarch grammar definition for the DatumIngest SQL dialect.
/// Monarch is Monaco Editor's built-in client-side tokenizer format; the
/// resulting object is serialized to JSON and used by the browser to provide
/// syntax highlighting without a server round-trip.
/// </summary>
internal static class MonarchGrammarFactory
{
    /// <summary>
    /// Constructs and returns the Monarch grammar as an anonymous object graph.
    /// The caller serializes this to JSON and sends it to the client.
    /// </summary>
    /// <remarks>
    /// Token type names follow Monaco's standard naming conventions so they map
    /// automatically to editor theme colors without additional configuration:
    /// <list type="bullet">
    ///   <item><c>keyword</c> — SQL clause and operator keywords (blue in most themes)</item>
    ///   <item><c>keyword.constant</c> — TRUE, FALSE, NULL (distinct color in most themes)</item>
    ///   <item><c>string</c> — single-quoted string literals</item>
    ///   <item><c>number</c> — integer and floating-point literals</item>
    ///   <item><c>variable</c> — named parameter placeholders ($name)</item>
    ///   <item><c>comment</c> — line comments (--) and block comments (/* */)</item>
    ///   <item><c>operator</c> — arithmetic and comparison symbols</item>
    ///   <item><c>delimiter</c> — commas, parentheses, dots</item>
    ///   <item><c>type.identifier</c> — column data type names (Int32, Float64, String, etc.)</item>
    ///   <item><c>predefined.function</c> — built-in function names (count, sum, abs, etc.)</item>
    ///   <item><c>identifier</c> — unquoted and double-quoted identifiers (default)</item>
    /// </list>
    /// Token rules are intentionally ordered: multi-character operators before
    /// single-character ones, literals before identifiers, identifiers last so
    /// keyword matching via the <c>@keywords</c> case table takes precedence.
    /// </remarks>
    internal static object Build() => new
    {
        defaultToken = "identifier",
        ignoreCase = true,
        keywords = ClauseKeywords(),
        boolNullKeywords = new[] { "TRUE", "FALSE", "NULL" },
        typeKeywords = TypeKeywords(),
        datePartKeywords = DatePartKeywords(),
        builtinFunctions = BuiltinFunctions(),
        tokenizer = new
        {
            root = new object[]
            {
                // Whitespace and comments are delegated to a sub-state so they
                // work regardless of position in the input.
                new { @include = "@whitespace" },

                // Single-quoted string literals. '' is the escape sequence for a
                // literal single quote inside a string.
                new[] { @"'([^'\\]|'')*'", "string" },

                // Numeric literals: integer, decimal, and scientific notation.
                new[] { @"\d+(\.\d*)?([eE][+-]?\d+)?", "number" },

                // Named parameter placeholders: $identifier
                new[] { @"\$[a-zA-Z_]\w*", "variable" },

                // Double-quoted identifiers: "column name". "" is the escape sequence.
                new[] { @"""([^""\\]|"""")*""", "identifier" },

                // Unquoted identifiers and keywords. The @keywords and @boolNullKeywords
                // case tables are checked first; anything else is a plain identifier.
                new object[]
                {
                    @"[a-zA-Z_]\w*",
                    new
                    {
                        cases = new Dictionary<string, string>
                        {
                            ["@boolNullKeywords"] = "keyword.constant",
                            ["@typeKeywords"] = "type.identifier",
                            ["@datePartKeywords"] = "attribute.name",
                            ["@keywords"] = "keyword",
                            ["@builtinFunctions"] = "predefined.function",
                            ["@default"] = "identifier",
                        }
                    }
                },

                // Multi-character comparison operators must precede single-character ones.
                new[] { @"[!<>]=|<>", "operator" },
                new[] { @"[<>=]", "operator" },

                // Arithmetic and bitwise operators.
                new[] { @"[+\-*/%^|]", "operator" },

                // Punctuation delimiters.
                new[] { @"[,.()\[\]]", "delimiter" },
            },

            whitespace = new object[]
            {
                new[] { @"[ \t\r\n]+", "white" },
                // Line comments.
                new[] { @"--.*$", "comment" },
                // Block comments: transition to the @blockComment sub-state.
                new[] { @"/\*", "comment", "@blockComment" },
            },

            // Block comment sub-state: consume everything until */.
            blockComment = new object[]
            {
                new[] { @"\*/", "comment", "@pop" },
                new[] { @".", "comment" },
            },
        },
    };

    /// <summary>
    /// Returns the full list of SQL clause and operator keywords. TRUE, FALSE,
    /// and NULL are intentionally excluded — they are in <c>boolNullKeywords</c>
    /// so themes can color them distinctly from clause keywords.
    /// </summary>
    internal static string[] ClauseKeywords() =>
    [
        // Core DML and clause keywords
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "LATERAL", "APPLY", "ON", "WHERE", "AND", "OR",
        "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE", "IS",
        "AS", "SHARD", "GROUP", "HAVING", "QUALIFY", "ORDER", "BY", "ASC",
        "DESC", "LIMIT", "OFFSET", "CAST", "EXTRACT", "AT", "TIME", "ZONE",

        // Conditional expressions
        "CASE", "WHEN", "THEN", "ELSE", "END",

        // Window function keywords
        "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING",
        "FOLLOWING", "CURRENT",

        // Modifiers
        "EXISTS", "DISTINCT", "IGNORE", "RESPECT", "NULLS",

        // Common Table Expressions
        "WITH", "RECURSIVE", "MATERIALIZED",

        // Set operations
        "UNION", "ALL", "INTERSECT", "EXCEPT",

        // DatumIngest extensions
        "LET", "SCAN", "INIT", "PIVOT", "UNPIVOT", "FOR", "INCLUDE",

        // ASSERT / DEFINE clause keywords
        "ASSERT", "DEFINE", "MESSAGE", "FAIL", "WARN", "SKIP", "ABORT",

        // DDL keywords
        "CREATE", "TABLE", "TEMP", "TEMPORARY", "DROP", "ALTER", "ADD",
        "COLUMN", "DEFAULT", "PRIMARY", "KEY", "IF", "INDEX",
        "ANALYZE",

        // DML keywords
        "INSERT", "VALUES", "UPDATE", "SET", "DELETE",
    ];

    /// <summary>
    /// Returns date part field names used with <c>EXTRACT(field FROM source)</c> and
    /// <c>date_part('field', source)</c>. Tokenized as <c>attribute.name</c> so they
    /// are visually distinct from plain identifiers. Names that overlap with SQL keywords
    /// or built-in functions (e.g. <c>YEAR</c>, <c>MONTH</c>) are excluded — they already
    /// get keyword or function coloring via earlier case rules.
    /// </summary>
    internal static string[] DatePartKeywords() =>
    [
        // PostgreSQL EXTRACT fields not already covered by keywords or built-in functions
        "DOW", "DOY", "ISODOW", "ISOYEAR",
        "EPOCH", "JULIAN",
        "CENTURY", "DECADE", "MILLENNIUM",
        "MICROSECOND", "MILLISECOND",
        "TIMEZONE", "TIMEZONE_HOUR", "TIMEZONE_MINUTE",
    ];

    /// <summary>
    /// Returns column data type names. These are tokenized as <c>type.identifier</c>
    /// so editor themes can color them distinctly from keywords and plain identifiers.
    /// </summary>
    internal static string[] TypeKeywords() =>
    [
        "Unknown",
        "Type",
        "Boolean",
        "UInt8", "UInt16", "UInt32", "UInt64",
        "Int8", "Int16", "Int32", "Int64",
        "Float32", "Float64",
        "Date", "Time", "DateTime", "Duration",
        "String", "JsonValue", "Uuid",
        "UInt8Array", "Image",
        "Vector", "Matrix", "Tensor", "Array", "Struct",
    ];

    /// <summary>
    /// Returns built-in function names. These are tokenized as <c>predefined.function</c>
    /// so editor themes can color them distinctly from plain identifiers.
    /// Names that overlap with SQL keywords (e.g. LEFT, RIGHT, CAST, RANGE)
    /// are intentionally excluded — the <c>@keywords</c> case takes precedence in the
    /// Monarch grammar, so they will be colored as keywords regardless.
    /// </summary>
    internal static string[] BuiltinFunctions() =>
    [
        // Aggregate functions
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "VARIANCE", "VAR_SAMP", "VAR_POP",
        "STDDEV", "STDDEV_SAMP", "STDDEV_POP",
        "MEDIAN", "PERCENTILE_CONT", "PERCENTILE_DISC", "MODE",
        "CORR", "COVAR_POP", "COVAR_SAMP",
        "APPROX_MEDIAN", "APPROX_PERCENTILE",
        "STRING_AGG", "ARRAY_AGG", "ARG_MAX", "ARG_MIN",

        // Window functions
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE",

        // Table-valued functions
        "UNNEST",

        // String functions
        "len", "mid", "substring", "upper", "lower",
        "trim", "ltrim", "rtrim", "btrim",
        "contains", "starts_with", "ends_with", "position", "replace",
        "concat", "concat_ws", "repeat", "reverse",
        "lpad", "rpad", "regexp_extract", "regexp_replace",
        "word_count", "split_part", "initcap", "translate",
        "ascii", "chr",
        "get_filename", "get_file_extension", "get_path",

        // Date/Time functions
        "now", "year", "month", "day", "hour", "minute", "second",
        "quarter", "dayofweek", "dayofyear",
        "make_date", "make_timestamp", "make_time", "current_time",
        "date_diff", "date_add", "date_trunc", "date_bucket", "date_bin",
        "date_span", "date_offset", "time_diff",
        "strftime", "is_date",

        // Duration functions
        "make_duration", "duration_seconds", "duration_minutes",
        "duration_hours", "duration_days",

        // Type conversion / introspection functions
        "to_epoch", "date_part", "cyclical_encode", "typeof", "can_cast", "try_cast",

        // JSON functions
        "json_value", "json_query", "json_exists", "json_array_length",

        // Math — Arithmetic & Basics
        "abs", "sign", "negate", "mod",
        "add", "subtract", "multiply", "divide",

        // Math — Powers/Roots/Logs
        "sqrt", "cbrt", "square", "exp", "exp2",
        "ln", "log2", "log10", "pow", "log",

        // Math — Trigonometric & Hyperbolic
        "sin", "cos", "tan", "asin", "acos", "atan", "atan2",
        "sinh", "cosh", "tanh", "degrees", "radians",
        "pi", "euler",

        // Math — Rounding & Quantization
        "ceil", "floor", "truncate", "round",
        "quantize", "bucketize", "clip",

        // Math — ML Activations
        "sigmoid", "relu", "selu", "gelu", "swish",
        "softplus", "softsign", "mish",
        "hard_sigmoid", "hard_swish", "leaky_relu", "elu",
        "softmax", "log_softmax", "l2_normalize",

        // Math — Vector Reductions
        "vec_sum", "vec_mean", "vec_min", "vec_max",
        "vec_std", "vec_var", "vec_median",
        "vec_argmin", "vec_argmax", "vec_norm",
        "vec_count_nonzero", "vec_any", "vec_all", "vec_product",

        // Tensor introspection
        "rdim", "shape",

        // Vector/Tensor manipulation
        "vec", "tensor", "vec_slice", "vec_concat",
        "vec_reverse", "vec_sort", "vec_unique",
        "vec_flatten", "vec_pad", "vec_repeat",
        "linspace", "arange",

        // Distance/Similarity
        "cosine_similarity", "euclidean_distance",
        "manhattan_distance", "dot", "hamming_distance",

        // Utility & Conditional
        "nullif", "coalesce", "greatest", "least",
        "is_nan", "is_finite", "is_even", "is_odd",
        "if_null", "iif", "choose",

        // Random & Sampling
        "random", "hash_split", "random_int", "random_range",
        "random_normal", "random_boolean",
        "random_truncated_normal", "random_log_normal",
        "random_exponential", "random_beta",
        "random_poisson", "random_categorical",
        "random_vector", "random_normal_vector",
        "random_permutation", "random_choice",

        // Numeric/Array
        "normalize", "clamp", "denormalize", "reshape",

        // Array functions
        "array", "array_length", "array_get",
        "array_contains", "array_position", "array_join",
        "array_concat", "array_slice", "array_sort",
        "array_reverse", "array_distinct",
        "array_min", "array_max", "array_sum", "array_avg",
        "array_transform", "array_filter",

        // Byte functions
        "bytes", "bytes_concat", "bytes_slice",

        // Categorical/Encoding
        "one_hot", "one_hot_unk",
        "label_encode", "label_encode_unk", "hash_encode",

        // Hash functions
        "md5", "sha256", "sha512", "crc32",

        // Encoding functions
        "base64_encode", "base64_decode",
        "hex_encode", "hex_decode",

        // UUID functions
        "uuidv4", "gen_random_uuid", "uuidv7", "is_uuid",
        "uuid_str", "uuid_bytes", "uuid_extract_version", "uuid_extract_timestamp",

        // Image — Metadata
        "width", "height", "channels", "pixel_count", "dimensions",

        // Image — Loading & Decode
        "load_image", "image_to_bytes",
        "image_to_tensor_hwc", "image_to_tensor_chw",

        // Image — Analysis
        "image_brightness_mean", "image_brightness_std",
        "image_brightness_histogram",
        "detect_blur", "compression_artifact_score",
        "image_pixel_mean", "image_pixel_std",

        // Image — Transforms
        "resize", "crop", "grayscale", "rotate",
        "noise", "blur", "brighten", "darken", "sobel",
        "resize_and_crop", "affine_transform",
        "elastic_deform", "perspective_warp",

        // Image — Hashing
        "perceptual_hash",
    ];
}
