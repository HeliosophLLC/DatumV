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

        // JSON
        registry.RegisterScalar(new Scalar.JsonValueFunction());
        registry.RegisterScalar(new Scalar.JsonQueryFunction());
        registry.RegisterScalar(new Scalar.JsonExistsFunction());
        registry.RegisterScalar(new Scalar.JsonArrayLengthFunction());

        // Table-valued
        registry.RegisterTableValued(new TableValued.UnnestFunction());

        return registry;
    }
}
