namespace DatumIngest.Functions;

/// <summary>
/// Registry for looking up scalar, table-valued, and aggregate functions by name.
/// Function names are matched case-insensitively.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, IScalarFunction> _scalarFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITableValuedFunction> _tableValuedFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAggregateFunction> _aggregateFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IWindowFunction> _windowFunctions = new(StringComparer.OrdinalIgnoreCase);

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
    /// Registers an existing scalar function under an additional alias name.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the alias name is already registered.</exception>
    public void RegisterScalarAlias(string alias, IScalarFunction function)
    {
        if (!_scalarFunctions.TryAdd(alias, function))
        {
            throw new ArgumentException($"Scalar function '{alias}' is already registered.");
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
    /// Registers an aggregate function.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same name is already registered.</exception>
    public void RegisterAggregate(IAggregateFunction function)
    {
        if (!_aggregateFunctions.TryAdd(function.Name, function))
        {
            throw new ArgumentException($"Aggregate function '{function.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a window function.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same name is already registered.</exception>
    public void RegisterWindow(IWindowFunction function)
    {
        if (!_windowFunctions.TryAdd(function.Name, function))
        {
            throw new ArgumentException($"Window function '{function.Name}' is already registered.");
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
    /// Looks up an aggregate function by name.
    /// </summary>
    /// <returns>The function, or null if not found.</returns>
    public IAggregateFunction? TryGetAggregate(string name)
    {
        _aggregateFunctions.TryGetValue(name, out IAggregateFunction? function);
        return function;
    }

    /// <summary>
    /// Looks up a dedicated window function by name.
    /// </summary>
    /// <returns>The window function, or null if not found.</returns>
    public IWindowFunction? TryGetWindow(string name)
    {
        _windowFunctions.TryGetValue(name, out IWindowFunction? function);
        return function;
    }

    /// <summary>
    /// Resolves a window function by name, checking the dedicated window registry
    /// first, then falling back to wrapping an aggregate function with
    /// <see cref="Window.AggregateWindowAdapter"/> if one exists.
    /// </summary>
    /// <returns>The window function, or null if neither a window nor aggregate function is found.</returns>
    public IWindowFunction? TryGetWindowOrAggregate(string name)
    {
        if (_windowFunctions.TryGetValue(name, out IWindowFunction? windowFunction))
        {
            return windowFunction;
        }

        if (_aggregateFunctions.TryGetValue(name, out IAggregateFunction? aggregateFunction))
        {
            return new Window.AggregateWindowAdapter(aggregateFunction);
        }

        return null;
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
    /// Returns all registered aggregate function names.
    /// </summary>
    public IEnumerable<string> AggregateFunctionNames => _aggregateFunctions.Keys;

    /// <summary>
    /// Returns all registered window function names.
    /// </summary>
    public IEnumerable<string> WindowFunctionNames => _windowFunctions.Keys;

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
        var len = new Scalar.LenFunction();
        registry.RegisterScalar(len);
        registry.RegisterScalarAlias("length", len);
        registry.RegisterScalarAlias("char_length", len);
        registry.RegisterScalarAlias("character_length", len);
        registry.RegisterScalar(new Scalar.MidFunction());
        registry.RegisterScalar(new Scalar.SubstringFunction());
        registry.RegisterScalar(new Scalar.GetFilenameFunction());
        registry.RegisterScalar(new Scalar.GetFileExtensionFunction());
        registry.RegisterScalar(new Scalar.GetPathFunction());
        registry.RegisterScalar(new Scalar.UpperFunction());
        registry.RegisterScalar(new Scalar.LowerFunction());
        registry.RegisterScalar(new Scalar.TrimFunction());
        registry.RegisterScalar(new Scalar.LtrimFunction());
        registry.RegisterScalar(new Scalar.RtrimFunction());
        registry.RegisterScalar(new Scalar.ContainsFunction());
        registry.RegisterScalar(new Scalar.StartsWithFunction());
        registry.RegisterScalar(new Scalar.EndsWithFunction());
        registry.RegisterScalar(new Scalar.PositionFunction());
        registry.RegisterScalar(new Scalar.ReplaceFunction());
        registry.RegisterScalar(new Scalar.ConcatFunction());
        registry.RegisterScalar(new Scalar.RepeatFunction());
        registry.RegisterScalar(new Scalar.ReverseFunction());
        registry.RegisterScalar(new Scalar.LeftFunction());
        registry.RegisterScalar(new Scalar.RightFunction());
        registry.RegisterScalar(new Scalar.LpadFunction());
        registry.RegisterScalar(new Scalar.RpadFunction());
        registry.RegisterScalar(new Scalar.RegexpExtractFunction());
        registry.RegisterScalar(new Scalar.RegexpReplaceFunction());
        registry.RegisterScalar(new Scalar.WordCountFunction());
        registry.RegisterScalar(new Scalar.ConcatWsFunction());
        registry.RegisterScalar(new Scalar.SplitPartFunction());
        registry.RegisterScalar(new Scalar.InitcapFunction());
        registry.RegisterScalar(new Scalar.TranslateFunction());
        registry.RegisterScalar(new Scalar.AsciiFunction());
        registry.RegisterScalar(new Scalar.ChrFunction());
        registry.RegisterScalar(new Scalar.BtrimFunction());

        // Type conversion
        registry.RegisterScalar(new Scalar.CastFunction());
        registry.RegisterScalar(new Scalar.ToEpochFunction());
        registry.RegisterScalar(new Scalar.DatePartFunction());
        registry.RegisterScalar(new Scalar.CyclicalEncodeFunction());

        // Date/Time — Extraction
        registry.RegisterScalar(new Scalar.YearFunction());
        registry.RegisterScalar(new Scalar.MonthFunction());
        registry.RegisterScalar(new Scalar.DayFunction());
        registry.RegisterScalar(new Scalar.HourFunction());
        registry.RegisterScalar(new Scalar.MinuteFunction());
        registry.RegisterScalar(new Scalar.SecondFunction());
        registry.RegisterScalar(new Scalar.QuarterFunction());
        registry.RegisterScalar(new Scalar.DayOfWeekFunction());
        registry.RegisterScalar(new Scalar.DayOfYearFunction());

        // Date/Time — Construction & Arithmetic
        registry.RegisterScalar(new Scalar.NowFunction());
        registry.RegisterScalar(new Scalar.MakeDateFunction());
        registry.RegisterScalar(new Scalar.MakeTimestampFunction());
        registry.RegisterScalar(new Scalar.MakeTimeFunction());
        registry.RegisterScalar(new Scalar.CurrentTimeFunction());
        registry.RegisterScalar(new Scalar.TransactionTimestampFunction());
        registry.RegisterScalar(new Scalar.StatementTimestampFunction());
        registry.RegisterScalar(new Scalar.ClockTimestampFunction());
        registry.RegisterScalar(new Scalar.TimeofdayFunction());
        registry.RegisterScalar(new Scalar.DateDiffFunction());
        registry.RegisterScalar(new Scalar.DateAddFunction());
        registry.RegisterScalar(new Scalar.DateTruncFunction());
        registry.RegisterScalar(new Scalar.DateBucketFunction());
        registry.RegisterScalar(new Scalar.DateBinFunction());
        registry.RegisterScalar(new Scalar.DateSpanFunction());
        registry.RegisterScalar(new Scalar.DateOffsetFunction());
        registry.RegisterScalar(new Scalar.TimeDiffFunction());

        // Date/Time — Formatting & Probing
        registry.RegisterScalar(new Scalar.StrftimeFunction());
        registry.RegisterScalar(new Scalar.IsDateFunction());

        // UUID
        var uuidv4 = new Scalar.Uuidv4Function();
        registry.RegisterScalar(uuidv4);
        registry.RegisterScalarAlias("gen_random_uuid", uuidv4);
        registry.RegisterScalar(new Scalar.Uuidv7Function());
        registry.RegisterScalar(new Scalar.IsUuidFunction());
        registry.RegisterScalar(new Scalar.UuidStrFunction());
        registry.RegisterScalar(new Scalar.UuidBytesFunction());
        registry.RegisterScalar(new Scalar.UuidExtractVersionFunction());
        registry.RegisterScalar(new Scalar.UuidExtractTimestampFunction());

        // JSON
        registry.RegisterScalar(new Scalar.JsonValueFunction());
        registry.RegisterScalar(new Scalar.JsonQueryFunction());
        registry.RegisterScalar(new Scalar.JsonExistsFunction());
        registry.RegisterScalar(new Scalar.JsonArrayLengthFunction());

        // Array
        registry.RegisterScalar(new Scalar.ArrayLengthFunction());
        registry.RegisterScalar(new Scalar.ArrayJoinFunction());
        registry.RegisterScalar(new Scalar.ArrayContainsFunction());
        registry.RegisterScalar(new Scalar.ArrayPositionFunction());
        registry.RegisterScalar(new Scalar.ArrayConstructorFunction());
        registry.RegisterScalar(new Scalar.ArraySortFunction());
        registry.RegisterScalar(new Scalar.ArrayReverseFunction());
        registry.RegisterScalar(new Scalar.ArrayDistinctFunction());
        registry.RegisterScalar(new Scalar.ArraySliceFunction());
        registry.RegisterScalar(new Scalar.ArrayConcatFunction());
        registry.RegisterScalar(new Scalar.ArrayGetFunction());
        registry.RegisterScalar(new Scalar.ArrayMinFunction());
        registry.RegisterScalar(new Scalar.ArrayMaxFunction());
        registry.RegisterScalar(new Scalar.ArraySumFunction());
        registry.RegisterScalar(new Scalar.ArrayAvgFunction());
        registry.RegisterScalar(new Scalar.ArrayTransformFunction());
        registry.RegisterScalar(new Scalar.ArrayFilterFunction());

        // Byte Array
        registry.RegisterScalar(new Scalar.BytesConcatFunction());
        registry.RegisterScalar(new Scalar.BytesSliceFunction());
        registry.RegisterScalar(new Scalar.BytesFunction());

        // Hashing
        registry.RegisterScalar(new Scalar.Md5Function());
        registry.RegisterScalar(new Scalar.Sha256Function());
        registry.RegisterScalar(new Scalar.Sha512Function());
        registry.RegisterScalar(new Scalar.Crc32Function());

        // Encoding
        registry.RegisterScalar(new Scalar.Base64EncodeFunction());
        registry.RegisterScalar(new Scalar.Base64DecodeFunction());
        registry.RegisterScalar(new Scalar.HexEncodeFunction());
        registry.RegisterScalar(new Scalar.HexDecodeFunction());

        // Duration
        registry.RegisterScalar(new Scalar.MakeDurationFunction());
        registry.RegisterScalar(new Scalar.DurationSecondsFunction());
        registry.RegisterScalar(new Scalar.DurationMinutesFunction());
        registry.RegisterScalar(new Scalar.DurationHoursFunction());
        registry.RegisterScalar(new Scalar.DurationDaysFunction());

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

        // Math — Tensor Introspection
        registry.RegisterScalar(new Math.RankFunction());
        registry.RegisterScalar(new Math.RdimFunction());
        registry.RegisterScalar(new Math.ShapeFunction());

        // Math — Vector Manipulation
        registry.RegisterScalar(new Math.VecFunction());
        registry.RegisterScalar(new Math.TensorFunction());
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
        registry.RegisterScalar(new Math.NullifFunction());
        registry.RegisterScalar(new Math.CoalesceFunction());
        registry.RegisterScalar(new Math.GreatestFunction());
        registry.RegisterScalar(new Math.LeastFunction());
        registry.RegisterScalar(new Math.IsNanFunction());
        registry.RegisterScalar(new Math.IsFiniteFunction());
        registry.RegisterScalar(new Math.IsEvenFunction());
        registry.RegisterScalar(new Math.IsOddFunction());
        registry.RegisterScalar(new Math.IfNullFunction());
        registry.RegisterScalar(new Math.IifFunction());
        registry.RegisterScalar(new Math.ChooseFunction());
        registry.RegisterScalar(new Math.RandomFunction());

        // Random — Core
        registry.RegisterScalar(new Math.HashSplitFunction());
        registry.RegisterScalar(new Math.RandomIntFunction());
        registry.RegisterScalar(new Math.RandomRangeFunction());
        registry.RegisterScalar(new Math.RandomNormalFunction());
        registry.RegisterScalar(new Math.RandomBooleanFunction());

        // Random — Distributions
        registry.RegisterScalar(new Math.RandomTruncatedNormalFunction());
        registry.RegisterScalar(new Math.RandomLogNormalFunction());
        registry.RegisterScalar(new Math.RandomExponentialFunction());
        registry.RegisterScalar(new Math.RandomBetaFunction());
        registry.RegisterScalar(new Math.RandomPoissonFunction());
        registry.RegisterScalar(new Math.RandomCategoricalFunction());

        // Random — Vector
        registry.RegisterScalar(new Math.RandomVectorFunction());
        registry.RegisterScalar(new Math.RandomNormalVectorFunction());
        registry.RegisterScalar(new Math.RandomPermutationFunction());
        registry.RegisterScalar(new Math.RandomChoiceFunction());

        // Categorical Encoding
        registry.RegisterScalar(new Scalar.OneHotFunction());
        registry.RegisterScalar(new Scalar.OneHotUnknownFunction());
        registry.RegisterScalar(new Scalar.LabelEncodeFunction());
        registry.RegisterScalar(new Scalar.LabelEncodeUnknownFunction());
        registry.RegisterScalar(new Scalar.HashEncodeFunction());

        // Image — Metadata
        registry.RegisterScalar(new Image.ImageWidthFunction());
        registry.RegisterScalar(new Image.ImageHeightFunction());
        registry.RegisterScalar(new Image.ImageChannelsFunction());
        registry.RegisterScalar(new Image.ImagePixelCountFunction());
        registry.RegisterScalar(new Image.ImageDimensionsFunction());

        // Image — Loading & Decode
        registry.RegisterScalar(new Image.LoadImageFunction());
        registry.RegisterScalar(new Image.ImageToBytesFunction());
        registry.RegisterScalar(new Image.ImageToTensorHwcFunction());
        registry.RegisterScalar(new Image.ImageToTensorChwFunction());

        // Image — Analysis
        registry.RegisterScalar(new Image.ImageBrightnessMeanFunction());
        registry.RegisterScalar(new Image.ImageBrightnessStandardDeviationFunction());
        registry.RegisterScalar(new Image.ImageBrightnessHistogramFunction());
        registry.RegisterScalar(new Image.DetectBlurFunction());
        registry.RegisterScalar(new Image.CompressionArtifactScoreFunction());

        // Image — Pixel Statistics
        registry.RegisterScalar(new Image.ImagePixelMeanFunction());
        registry.RegisterScalar(new Image.ImagePixelStandardDeviationFunction());

        // Image — Transforms
        registry.RegisterScalar(new Image.ResizeImageFunction());
        registry.RegisterScalar(new Image.CropImageFunction());
        registry.RegisterScalar(new Image.GrayscaleImageFunction());
        registry.RegisterScalar(new Image.RotateImageFunction());
        registry.RegisterScalar(new Image.NoiseImageFunction());
        registry.RegisterScalar(new Image.BlurImageFunction());
        registry.RegisterScalar(new Image.BrightenImageFunction());
        registry.RegisterScalar(new Image.DarkenImageFunction());
        registry.RegisterScalar(new Image.SobelImageFunction());
        registry.RegisterScalar(new Image.ResizeAndCropImageFunction());
        registry.RegisterScalar(new Image.AffineTransformFunction());
        registry.RegisterScalar(new Image.ElasticDeformFunction());
        registry.RegisterScalar(new Image.PerspectiveWarpFunction());

        // Image — Hashing
        registry.RegisterScalar(new Image.PerceptualHashFunction());

        // Type introspection
        registry.RegisterScalar(new Scalar.TypeofFunction());
        registry.RegisterScalar(new Scalar.CanCastFunction());
        registry.RegisterScalar(new Scalar.TryCastFunction());

        // Table-valued
        registry.RegisterTableValued(new TableValued.UnnestFunction());
        registry.RegisterTableValued(new TableValued.RangeFunction());

        // Aggregate
        registry.RegisterAggregate(new Aggregates.CountFunction());
        registry.RegisterAggregate(new Aggregates.SumFunction());
        registry.RegisterAggregate(new Aggregates.AvgFunction());
        registry.RegisterAggregate(new Aggregates.MinFunction());
        registry.RegisterAggregate(new Aggregates.MaxFunction());
        registry.RegisterAggregate(new Aggregates.VarianceFunction(usePopulation: false, "VARIANCE"));
        registry.RegisterAggregate(new Aggregates.VarianceFunction(usePopulation: false, "VAR_SAMP"));
        registry.RegisterAggregate(new Aggregates.VarianceFunction(usePopulation: true, "VAR_POP"));
        registry.RegisterAggregate(new Aggregates.StandardDeviationFunction(usePopulation: false, "STDDEV"));
        registry.RegisterAggregate(new Aggregates.StandardDeviationFunction(usePopulation: false, "STDDEV_SAMP"));
        registry.RegisterAggregate(new Aggregates.StandardDeviationFunction(usePopulation: true, "STDDEV_POP"));
        registry.RegisterAggregate(new Aggregates.MedianFunction());
        registry.RegisterAggregate(new Aggregates.PercentileContinuousFunction());
        registry.RegisterAggregate(new Aggregates.PercentileDiscreteFunction());
        registry.RegisterAggregate(new Aggregates.ModeFunction());
        registry.RegisterAggregate(new Aggregates.CorrelationFunction());
        registry.RegisterAggregate(new Aggregates.CovarianceFunction(usePopulation: true, "COVAR_POP"));
        registry.RegisterAggregate(new Aggregates.CovarianceFunction(usePopulation: false, "COVAR_SAMP"));
        registry.RegisterAggregate(new Aggregates.ApproximateMedianFunction());
        registry.RegisterAggregate(new Aggregates.ApproximatePercentileFunction());
        registry.RegisterAggregate(new Aggregates.StringAggregateFunction());
        registry.RegisterAggregate(new Aggregates.ArrayAggregateFunction());
        registry.RegisterAggregate(new Aggregates.ArgMaxFunction(findMaximum: true, "ARG_MAX"));
        registry.RegisterAggregate(new Aggregates.ArgMaxFunction(findMaximum: false, "ARG_MIN"));

        // Window
        registry.RegisterWindow(new Window.RowNumberFunction());
        registry.RegisterWindow(new Window.RankFunction());
        registry.RegisterWindow(new Window.DenseRankFunction());
        registry.RegisterWindow(new Window.NtileFunction());
        registry.RegisterWindow(new Window.LagFunction());
        registry.RegisterWindow(new Window.LeadFunction());
        registry.RegisterWindow(new Window.FirstValueFunction());
        registry.RegisterWindow(new Window.LastValueFunction());
        registry.RegisterWindow(new Window.NthValueFunction());

        return registry;
    }
}
