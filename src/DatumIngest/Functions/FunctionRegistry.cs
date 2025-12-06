namespace DatumIngest.Functions;

/// <summary>
/// Registry for looking up scalar, table-valued, aggregate, and window functions
/// by name. Function names are matched case-insensitively. Scalar registrations
/// use the static-abstract metadata on <see cref="IScalarFunction"/> to build a
/// <see cref="FunctionDescriptor"/> at registration time, so catalog tooling can
/// describe the registered set without instantiating each function.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, IScalarFunction> _scalarFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FunctionDescriptor> _scalarDescriptorsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FunctionDescriptor> _scalarDescriptors = new();
    private readonly Dictionary<string, ITableValuedFunction> _tableValuedFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAggregateFunction> _aggregateFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IWindowFunction> _windowFunctions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a scalar function described by <typeparamref name="T"/>'s
    /// static-abstract metadata. Reads <c>T.Name</c>, <c>T.Category</c>,
    /// <c>T.Description</c>, and <c>T.Signatures</c> at registration time.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same name is already registered.</exception>
    public void RegisterScalar<T>() where T : IFunction, IScalarFunction, new()
    {
        T instance = new();
        FunctionDescriptor descriptor = new(
            PrimaryName: T.Name,
            Aliases: Array.Empty<string>(),
            Category: T.Category,
            Description: T.Description,
            Signatures: T.Signatures);

        if (!_scalarFunctions.TryAdd(T.Name, instance))
        {
            throw new ArgumentException($"Scalar function '{T.Name}' is already registered.");
        }
        _scalarDescriptorsByName[T.Name] = descriptor;
        _scalarDescriptors.Add(descriptor);
    }

    /// <summary>
    /// Registers an existing scalar function under an additional alias name.
    /// </summary>
    /// <exception cref="ArgumentException">No primary registration for <typeparamref name="T"/> exists, or the alias is already taken.</exception>
    public void RegisterScalarAlias<T>(string alias) where T : IFunction, IScalarFunction
    {
        if (!_scalarFunctions.TryGetValue(T.Name, out IScalarFunction? primary))
        {
            throw new ArgumentException(
                $"Cannot register alias '{alias}' for {T.Name}: primary registration not found.",
                nameof(alias));
        }
        if (!_scalarFunctions.TryAdd(alias, primary))
        {
            throw new ArgumentException($"Scalar function '{alias}' is already registered.");
        }

        if (_scalarDescriptorsByName.TryGetValue(T.Name, out FunctionDescriptor? primaryDescriptor))
        {
            FunctionDescriptor updated = primaryDescriptor with
            {
                Aliases = [.. primaryDescriptor.Aliases, alias],
            };
            int idx = _scalarDescriptors.IndexOf(primaryDescriptor);
            if (idx >= 0)
            {
                _scalarDescriptors[idx] = updated;
            }
            _scalarDescriptorsByName[T.Name] = updated;
        }
        // Aliases also map back to the primary descriptor for lookup.
        _scalarDescriptorsByName[alias] = _scalarDescriptorsByName[T.Name];
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
    public IScalarFunction? TryGetScalar(string name)
    {
        _scalarFunctions.TryGetValue(name, out IScalarFunction? function);
        return function;
    }

    /// <summary>
    /// Looks up the catalog descriptor for a scalar function by name (or alias).
    /// </summary>
    public FunctionDescriptor? TryGetScalarDescriptor(string name)
    {
        _scalarDescriptorsByName.TryGetValue(name, out FunctionDescriptor? descriptor);
        return descriptor;
    }

    /// <summary>
    /// Looks up a table-valued function by name.
    /// </summary>
    public ITableValuedFunction? TryGetTableValued(string name)
    {
        _tableValuedFunctions.TryGetValue(name, out ITableValuedFunction? function);
        return function;
    }

    /// <summary>
    /// Looks up an aggregate function by name.
    /// </summary>
    public IAggregateFunction? TryGetAggregate(string name)
    {
        _aggregateFunctions.TryGetValue(name, out IAggregateFunction? function);
        return function;
    }

    /// <summary>
    /// Looks up a dedicated window function by name.
    /// </summary>
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
    /// Returns all registered scalar function names (including aliases).
    /// </summary>
    public IEnumerable<string> ScalarFunctionNames => _scalarFunctions.Keys;

    /// <summary>
    /// Returns the descriptor for every primary scalar registration.
    /// Aliases are reported via <see cref="FunctionDescriptor.Aliases"/>.
    /// </summary>
    public IReadOnlyList<FunctionDescriptor> ScalarDescriptors => _scalarDescriptors;

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
    /// Scalar/math functions are registered demand-pulled — the function
    /// rebuild deliberately starts with an empty scalar set and adds
    /// functions back as demos require them. See
    /// <c>memory/project_function_rebuild.md</c> for the rebuild plan.
    /// </summary>
    public static FunctionRegistry CreateDefault()
    {
        FunctionRegistry registry = new();

        // ── Scalar ────────────────────────────────────────────────────────
        // Demand-pulled rebuild: each function lands when a stage delivers it.
        // Stage 4: concat. Stage 5: upper / lower. Stage 6: cast / try_cast /
        // typeof. Anything beyond is added back when a demo demands it.
        registry.RegisterScalar<Scalar.ConcatFunction>();
        registry.RegisterScalar<Scalar.UpperFunction>();
        registry.RegisterScalar<Scalar.LowerFunction>();
        registry.RegisterScalar<Scalar.CastFunction>();
        registry.RegisterScalar<Scalar.TryCastFunction>();
        registry.RegisterScalar<Scalar.TypeofFunction>();
        registry.RegisterScalar<Scalar.Math.AbsFunction>();
        registry.RegisterScalar<Scalar.RandomStringFunction>();
        registry.RegisterScalar<Scalar.RandomStringFromSeedFunction>();
        registry.RegisterScalar<Scalar.RandomFloat32Function>();
        registry.RegisterScalar<Scalar.RandomFloat32FromSeedFunction>();

        // Json — parse, scalar lookup, subdocument query, text re-emit.
        // Backed by canonical CBOR in the arena; the codec lives in Functions/Json.
        registry.RegisterScalar<Scalar.Json.JsonParseFunction>();
        registry.RegisterScalar<Scalar.Json.JsonTryParseFunction>();
        registry.RegisterScalar<Scalar.Json.JsonValueFunction>();
        registry.RegisterScalar<Scalar.Json.JsonQueryFunction>();
        registry.RegisterScalar<Scalar.Json.JsonToTextFunction>();

        // Image — pipeline functions are still wired through the legacy
        // path; the image rework is out of scope for this rebuild.
        // (Re-enabled per-image-function once those are migrated.)

        // ── Table-valued ──────────────────────────────────────────────────
        // UNNEST retired pending the reference-type-array consolidation; will be
        // rebuilt on the new typed-array surface when a demand actually requires it.
        registry.RegisterTableValued(new TableValued.RangeFunction());

        // ── Aggregate ─────────────────────────────────────────────────────
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

        // ── Window ────────────────────────────────────────────────────────
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
