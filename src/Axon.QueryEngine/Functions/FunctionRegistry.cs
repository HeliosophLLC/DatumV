namespace Axon.QueryEngine.Functions;

/// <summary>
/// Registry for looking up scalar and table-valued functions by name.
/// Function names are matched case-insensitively.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, IScalarFunction> _scalarFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITableValuedFunction> _tableValuedFunctions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a scalar function.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same name is already registered.</exception>
    public void RegisterScalar(IScalarFunction function)
    {
        if (!_scalarFunctions.TryAdd(function.Name, function))
        {
            throw new ArgumentException($"Scalar function '{function.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a table-valued function.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same name is already registered.</exception>
    public void RegisterTableValued(ITableValuedFunction function)
    {
        if (!_tableValuedFunctions.TryAdd(function.Name, function))
        {
            throw new ArgumentException($"Table-valued function '{function.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Looks up a scalar function by name.
    /// </summary>
    /// <returns>The function, or null if not found.</returns>
    public IScalarFunction? TryGetScalar(string name)
    {
        _scalarFunctions.TryGetValue(name, out IScalarFunction? function);
        return function;
    }

    /// <summary>
    /// Looks up a table-valued function by name.
    /// </summary>
    /// <returns>The function, or null if not found.</returns>
    public ITableValuedFunction? TryGetTableValued(string name)
    {
        _tableValuedFunctions.TryGetValue(name, out ITableValuedFunction? function);
        return function;
    }

    /// <summary>
    /// Returns all registered scalar function names.
    /// </summary>
    public IEnumerable<string> ScalarFunctionNames => _scalarFunctions.Keys;

    /// <summary>
    /// Returns all registered table-valued function names.
    /// </summary>
    public IEnumerable<string> TableValuedFunctionNames => _tableValuedFunctions.Keys;

    /// <summary>
    /// Creates a registry pre-populated with all built-in functions.
    /// </summary>
    public static FunctionRegistry CreateDefault()
    {
        FunctionRegistry registry = new();

        // Numeric/Array
        registry.RegisterScalar(new Scalar.NormalizeFunction());
        registry.RegisterScalar(new Scalar.ClampFunction());
        registry.RegisterScalar(new Scalar.DenormalizeFunction());
        registry.RegisterScalar(new Scalar.ReshapeFunction());

        // String
        registry.RegisterScalar(new Scalar.LenFunction());
        registry.RegisterScalar(new Scalar.MidFunction());
        registry.RegisterScalar(new Scalar.SubstringFunction());
        registry.RegisterScalar(new Scalar.GetFilenameFunction());
        registry.RegisterScalar(new Scalar.GetFileExtensionFunction());
        registry.RegisterScalar(new Scalar.GetPathFunction());

        // Type conversion
        registry.RegisterScalar(new Scalar.CastFunction());
        registry.RegisterScalar(new Scalar.ToEpochFunction());
        registry.RegisterScalar(new Scalar.DatePartFunction());
        registry.RegisterScalar(new Scalar.CyclicalEncodeFunction());

        // JSON
        registry.RegisterScalar(new Scalar.JsonValueFunction());
        registry.RegisterScalar(new Scalar.JsonQueryFunction());
        registry.RegisterScalar(new Scalar.JsonExistsFunction());
        registry.RegisterScalar(new Scalar.JsonArrayLengthFunction());

        // Math — Arithmetic
        registry.RegisterScalar(new Math.AbsFunction());
        registry.RegisterScalar(new Math.SignFunction());
        registry.RegisterScalar(new Math.NegateFunction());
        registry.RegisterScalar(new Math.ModFunction());
        registry.RegisterScalar(new Math.AddFunction());
        registry.RegisterScalar(new Math.SubtractFunction());
        registry.RegisterScalar(new Math.MultiplyFunction());
        registry.RegisterScalar(new Math.DivideFunction());

        // Math — Powers/Roots/Logs
        registry.RegisterScalar(new Math.SqrtFunction());
        registry.RegisterScalar(new Math.CbrtFunction());
        registry.RegisterScalar(new Math.SquareFunction());
        registry.RegisterScalar(new Math.ExpFunction());
        registry.RegisterScalar(new Math.Exp2Function());
        registry.RegisterScalar(new Math.LnFunction());
        registry.RegisterScalar(new Math.Log2Function());
        registry.RegisterScalar(new Math.Log10Function());
        registry.RegisterScalar(new Math.PowFunction());
        registry.RegisterScalar(new Math.LogFunction());

        // Math — Trigonometric & Hyperbolic
        registry.RegisterScalar(new Math.SinFunction());
        registry.RegisterScalar(new Math.CosFunction());
        registry.RegisterScalar(new Math.TanFunction());
        registry.RegisterScalar(new Math.AsinFunction());
        registry.RegisterScalar(new Math.AcosFunction());
        registry.RegisterScalar(new Math.AtanFunction());
        registry.RegisterScalar(new Math.Atan2Function());
        registry.RegisterScalar(new Math.SinhFunction());
        registry.RegisterScalar(new Math.CoshFunction());
        registry.RegisterScalar(new Math.TanhFunction());
        registry.RegisterScalar(new Math.DegreesFunction());
        registry.RegisterScalar(new Math.RadiansFunction());
        registry.RegisterScalar(new Math.PiFunction());
        registry.RegisterScalar(new Math.EulerFunction());

        // Math — Rounding & Quantization
        registry.RegisterScalar(new Math.CeilFunction());
        registry.RegisterScalar(new Math.FloorFunction());
        registry.RegisterScalar(new Math.TruncateFunction());
        registry.RegisterScalar(new Math.RoundFunction());
        registry.RegisterScalar(new Math.QuantizeFunction());
        registry.RegisterScalar(new Math.BucketizeFunction());
        registry.RegisterScalar(new Math.ClipFunction());

        // Math — ML Activations
        registry.RegisterScalar(new Math.SigmoidFunction());
        registry.RegisterScalar(new Math.ReluFunction());
        registry.RegisterScalar(new Math.SeluFunction());
        registry.RegisterScalar(new Math.GeluFunction());
        registry.RegisterScalar(new Math.SwishFunction());
        registry.RegisterScalar(new Math.SoftplusFunction());
        registry.RegisterScalar(new Math.SoftsignFunction());
        registry.RegisterScalar(new Math.MishFunction());
        registry.RegisterScalar(new Math.HardSigmoidFunction());
        registry.RegisterScalar(new Math.HardSwishFunction());
        registry.RegisterScalar(new Math.LeakyReluFunction());
        registry.RegisterScalar(new Math.EluFunction());

        // Math — Softmax & Normalization
        registry.RegisterScalar(new Math.SoftmaxFunction());
        registry.RegisterScalar(new Math.LogSoftmaxFunction());
        registry.RegisterScalar(new Math.L2NormalizeFunction());

        // Math — Vector Reductions
        registry.RegisterScalar(new Math.VecSumFunction());
        registry.RegisterScalar(new Math.VecMeanFunction());
        registry.RegisterScalar(new Math.VecMinFunction());
        registry.RegisterScalar(new Math.VecMaxFunction());
        registry.RegisterScalar(new Math.VecStdFunction());
        registry.RegisterScalar(new Math.VecVarFunction());
        registry.RegisterScalar(new Math.VecMedianFunction());
        registry.RegisterScalar(new Math.VecArgminFunction());
        registry.RegisterScalar(new Math.VecArgmaxFunction());
        registry.RegisterScalar(new Math.VecNormFunction());
        registry.RegisterScalar(new Math.VecCountNonzeroFunction());
        registry.RegisterScalar(new Math.VecAnyFunction());
        registry.RegisterScalar(new Math.VecAllFunction());
        registry.RegisterScalar(new Math.VecProductFunction());

        // Math — Vector Manipulation
        registry.RegisterScalar(new Math.VecSliceFunction());
        registry.RegisterScalar(new Math.VecConcatFunction());
        registry.RegisterScalar(new Math.VecReverseFunction());
        registry.RegisterScalar(new Math.VecSortFunction());
        registry.RegisterScalar(new Math.VecUniqueFunction());
        registry.RegisterScalar(new Math.VecFlattenFunction());
        registry.RegisterScalar(new Math.VecPadFunction());
        registry.RegisterScalar(new Math.VecRepeatFunction());
        registry.RegisterScalar(new Math.LinspaceFunction());
        registry.RegisterScalar(new Math.ArangeFunction());

        // Math — Distance & Similarity
        registry.RegisterScalar(new Math.CosineSimilarityFunction());
        registry.RegisterScalar(new Math.EuclideanDistanceFunction());
        registry.RegisterScalar(new Math.ManhattanDistanceFunction());
        registry.RegisterScalar(new Math.DotFunction());
        registry.RegisterScalar(new Math.HammingDistanceFunction());

        // Math — Utility & Conditional
        registry.RegisterScalar(new Math.CoalesceFunction());
        registry.RegisterScalar(new Math.GreatestFunction());
        registry.RegisterScalar(new Math.LeastFunction());
        registry.RegisterScalar(new Math.IsNanFunction());
        registry.RegisterScalar(new Math.IsFiniteFunction());
        registry.RegisterScalar(new Math.IfNullFunction());
        registry.RegisterScalar(new Math.RandomFunction());

        // Table-valued
        registry.RegisterTableValued(new TableValued.UnnestFunction());
        registry.RegisterTableValued(new TableValued.RangeFunction());

        return registry;
    }
}
