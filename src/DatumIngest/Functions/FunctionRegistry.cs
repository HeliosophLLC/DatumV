using System.Collections.Concurrent;

using DatumIngest.Catalog;
using DatumIngest.Models;

namespace DatumIngest.Functions;

/// <summary>
/// Registry for looking up scalar, table-valued, aggregate, and window functions
/// by name. Entries live under a <see cref="QualifiedName"/> (schema + name);
/// built-ins register into the <c>system</c> schema. Lookup supports three
/// shapes:
/// <list type="bullet">
///   <item><description>Exact qualified — <see cref="TryGetScalar(QualifiedName)"/>.</description></item>
///   <item><description>Explicit-or-walk — <see cref="TryGetScalar(string?, string, IReadOnlyList{string})"/>:
///     an explicit schema goes straight to that schema; an unqualified name
///     walks the supplied <c>search_path</c> in order, first hit wins.</description></item>
///   <item><description>Back-compat bare-string — <see cref="TryGetScalar(string)"/>:
///     names containing a dot split into <c>(schema, name)</c> and exact-match;
///     bare names walk the default <c>[public, system]</c> path. Preserves
///     pre-S7 call sites that haven't been migrated to pass an explicit
///     search_path yet.</description></item>
/// </list>
/// </summary>
public sealed class FunctionRegistry
{
    /// <summary>Schema that built-in scalars / aggregates / window functions live in.</summary>
    public const string SystemSchema = "system";

    /// <summary>
    /// Default search_path applied by the bare-string lookup overloads.
    /// Mirrors <c>TableCatalog</c>'s default so unqualified function calls
    /// resolve the same way unqualified table references do.
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultSearchPath = new[] { "public", SystemSchema };

    private readonly Dictionary<QualifiedName, IScalarFunction> _scalarFunctions = new();
    private readonly Dictionary<QualifiedName, FunctionDescriptor> _scalarDescriptorsByName = new();
    private readonly List<FunctionDescriptor> _scalarDescriptors = new();
    private readonly Dictionary<QualifiedName, ITableValuedFunction> _tableValuedFunctions = new();
    private readonly Dictionary<QualifiedName, IAggregateFunction> _aggregateFunctions = new();
    private readonly Dictionary<QualifiedName, IWindowFunction> _windowFunctions = new();
    private readonly ConcurrentDictionary<QualifiedName, ModelScalarFunction> _resolvedModelFunctions = new();
    private Func<ModelCatalog?>? _modelCatalogResolver;

    /// <summary>
    /// Registers a scalar function described by <typeparamref name="T"/>'s
    /// static-abstract metadata. The function lands in <paramref name="schema"/>
    /// (defaults to <c>system</c> — every built-in lives there).
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same qualified name is already registered.</exception>
    public void RegisterScalar<T>(string schema = SystemSchema) where T : IFunction, IScalarFunction, new()
    {
        T instance = new();
        QualifiedName key = new(schema, T.Name);
        FunctionDescriptor descriptor = new(
            PrimaryName: T.Name,
            Aliases: Array.Empty<string>(),
            Category: T.Category,
            Description: T.Description,
            Signatures: T.Signatures,
            BodyScope: T.BodyScope,
            SchemaName: schema);

        if (!_scalarFunctions.TryAdd(key, instance))
        {
            throw new ArgumentException($"Scalar function '{key}' is already registered.");
        }
        _scalarDescriptorsByName[key] = descriptor;
        _scalarDescriptors.Add(descriptor);
    }

    /// <summary>
    /// Registers an existing scalar function under an additional alias name.
    /// The alias lives in the same schema as the primary.
    /// </summary>
    /// <exception cref="ArgumentException">No primary registration for <typeparamref name="T"/> exists in <paramref name="schema"/>, or the alias is already taken.</exception>
    public void RegisterScalarAlias<T>(string alias, string schema = SystemSchema) where T : IFunction, IScalarFunction
    {
        QualifiedName primaryKey = new(schema, T.Name);
        QualifiedName aliasKey = new(schema, alias);
        if (!_scalarFunctions.TryGetValue(primaryKey, out IScalarFunction? primary))
        {
            throw new ArgumentException(
                $"Cannot register alias '{aliasKey}' for {primaryKey}: primary registration not found.",
                nameof(alias));
        }
        if (!_scalarFunctions.TryAdd(aliasKey, primary))
        {
            throw new ArgumentException($"Scalar function '{aliasKey}' is already registered.");
        }

        if (_scalarDescriptorsByName.TryGetValue(primaryKey, out FunctionDescriptor? primaryDescriptor))
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
            _scalarDescriptorsByName[primaryKey] = updated;
        }
        // Aliases also map back to the primary descriptor for lookup.
        _scalarDescriptorsByName[aliasKey] = _scalarDescriptorsByName[primaryKey];
    }

    /// <summary>
    /// Registers a runtime-constructed scalar function instance under
    /// <paramref name="name"/>. Used for entries that don't have static-abstract
    /// metadata — currently the procedural-UDF adapter built per
    /// <c>CREATE FUNCTION … BEGIN…END</c> registration. <paramref name="descriptor"/>
    /// is optional; when supplied, it surfaces in <c>system.functions</c> alongside
    /// the type-registered scalars.
    /// </summary>
    /// <remarks>
    /// Accepts either a bare name (lands in <c>public</c>) or a dotted
    /// <c>schema.name</c> string. The current procedural-UDF adapter passes
    /// <c>"udf.X"</c> — a vestigial prefix that becomes a real schema slot
    /// in S7d when the inliner stops adding it.
    /// </remarks>
    /// <param name="name">Name to register under. May be qualified (<c>schema.fn</c>).</param>
    /// <param name="instance">The scalar function instance.</param>
    /// <param name="descriptor">Optional catalog descriptor for introspection.</param>
    /// <param name="replace">When <see langword="true"/>, overwrites any existing entry with the same name.</param>
    /// <exception cref="ArgumentException">A function with the same name is already registered and <paramref name="replace"/> is <see langword="false"/>.</exception>
    public void RegisterScalarInstance(
        string name,
        IScalarFunction instance,
        FunctionDescriptor? descriptor = null,
        bool replace = false)
    {
        QualifiedName key = QualifiedName.Parse(name, defaultSchema: "public");
        if (replace)
        {
            // When replacing, also drop the old descriptor so introspection
            // doesn't show stale metadata next to the new instance.
            if (_scalarDescriptorsByName.Remove(key, out FunctionDescriptor? old))
            {
                _scalarDescriptors.Remove(old);
            }
            _scalarFunctions[key] = instance;
        }
        else if (!_scalarFunctions.TryAdd(key, instance))
        {
            throw new ArgumentException($"Scalar function '{key}' is already registered.");
        }

        if (descriptor is not null)
        {
            // Stamp the schema on the descriptor from the parsed registry
            // key, so language-server completion can filter built-ins by
            // schema. Callers building descriptors by hand (chat templates,
            // procedural UDFs) usually default SchemaName to "system" and
            // the parsed key carries the truth.
            FunctionDescriptor schemaStamped = descriptor with { SchemaName = key.Schema };
            _scalarDescriptorsByName[key] = schemaStamped;
            _scalarDescriptors.Add(schemaStamped);
        }
    }

    /// <summary>
    /// Removes a previously registered scalar function. Returns
    /// <see langword="true"/> when an entry was removed, <see langword="false"/>
    /// when the name wasn't registered. Both the instance and any associated
    /// descriptor are dropped together so introspection stays consistent.
    /// </summary>
    public bool UnregisterScalar(string name)
    {
        QualifiedName key = QualifiedName.Parse(name, defaultSchema: "public");
        bool removedInstance = _scalarFunctions.Remove(key);
        if (_scalarDescriptorsByName.Remove(key, out FunctionDescriptor? descriptor))
        {
            _scalarDescriptors.Remove(descriptor);
        }
        return removedInstance;
    }

    /// <summary>
    /// Registers a table-valued function described by <typeparamref name="T"/>'s
    /// static-abstract metadata into <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same qualified name is already registered.</exception>
    public void RegisterTableValued<T>(string schema = SystemSchema) where T : ITableValuedFunctionMetadata, ITableValuedFunction, new()
    {
        T instance = new();
        QualifiedName key = new(schema, T.Name);
        if (!_tableValuedFunctions.TryAdd(key, instance))
        {
            throw new ArgumentException($"Table-valued function '{key}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a table-valued function instance directly into <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same qualified name is already registered.</exception>
    public void RegisterTableValued(ITableValuedFunction function, string schema = SystemSchema)
    {
        QualifiedName key = new(schema, function.Name);
        if (!_tableValuedFunctions.TryAdd(key, function))
        {
            throw new ArgumentException($"Table-valued function '{key}' is already registered.");
        }
    }

    /// <summary>
    /// Registers an aggregate function into <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same qualified name is already registered.</exception>
    public void RegisterAggregate(IAggregateFunction function, string schema = SystemSchema)
    {
        QualifiedName key = new(schema, function.Name);
        if (!_aggregateFunctions.TryAdd(key, function))
        {
            throw new ArgumentException($"Aggregate function '{key}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a window function into <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A function with the same qualified name is already registered.</exception>
    public void RegisterWindow(IWindowFunction function, string schema = SystemSchema)
    {
        QualifiedName key = new(schema, function.Name);
        if (!_windowFunctions.TryAdd(key, function))
        {
            throw new ArgumentException($"Window function '{key}' is already registered.");
        }
    }

    /// <summary>
    /// Configures a resolver for the host's <see cref="ModelCatalog"/> so
    /// <c>models.X(...)</c> calls dispatch through this registry as a
    /// fallback when the regular scalar lookup misses. The resolver is
    /// invoked lazily on each lookup so it sees the catalog's current state
    /// — important because <c>TableCatalog.Models</c> is a mutable property
    /// and may be (re)assigned after the registry is constructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hoisted query path (<c>ModelInvocationHoister</c> +
    /// <c>ModelInvocationOperator</c>) doesn't go through this fallback —
    /// the hoister extracts <c>models.X</c> calls before evaluation. This
    /// fallback exists so unhoisted contexts (procedural UDF bodies,
    /// directly-invoked expressions) can still reach the catalog.
    /// </para>
    /// </remarks>
    /// <param name="resolver">
    /// Callback returning the catalog (or <see langword="null"/> when no
    /// catalog is attached). Pass <see langword="null"/> to disable the
    /// fallback entirely.
    /// </param>
    public void SetModelCatalogResolver(Func<ModelCatalog?>? resolver)
    {
        _modelCatalogResolver = resolver;
        _resolvedModelFunctions.Clear();
    }

    // ──────────────────── Scalar lookup ────────────────────

    /// <summary>
    /// Exact lookup by qualified name. No search_path walk, no model
    /// fallback — used by call sites that have already resolved the
    /// schema or want to assert a specific schema membership.
    /// </summary>
    public IScalarFunction? TryGetScalar(QualifiedName name)
    {
        if (_scalarFunctions.TryGetValue(name, out IScalarFunction? function))
        {
            return function;
        }
        if (_resolvedModelFunctions.TryGetValue(name, out ModelScalarFunction? cached))
        {
            return cached;
        }
        if (TryResolveModelFunction(name, out ModelScalarFunction? resolved))
        {
            _resolvedModelFunctions[name] = resolved;
            return resolved;
        }
        return null;
    }

    /// <summary>
    /// Search-path-aware lookup. An explicit <paramref name="explicitSchema"/>
    /// goes straight to that schema; an unqualified name walks
    /// <paramref name="searchPath"/> in order and returns the first hit.
    /// </summary>
    public IScalarFunction? TryGetScalar(string? explicitSchema, string name, IReadOnlyList<string> searchPath)
    {
        if (explicitSchema is not null)
        {
            return TryGetScalar(new QualifiedName(explicitSchema, name));
        }

        foreach (string schema in searchPath)
        {
            IScalarFunction? hit = TryGetScalar(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Back-compat lookup used by pre-S7 call sites that pass a flat
    /// name. Strings containing a dot are split into <c>(schema, name)</c>
    /// and exact-matched (preserves the <c>"udf.X"</c> / <c>"models.X"</c>
    /// dispatch contract). Bare names walk the default
    /// <c>[public, system]</c> path. New call sites should pass an
    /// explicit <c>search_path</c> via the overload above.
    /// </summary>
    public IScalarFunction? TryGetScalar(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetScalar(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        return TryGetScalar(explicitSchema: null, name, DefaultSearchPath);
    }

    private bool TryResolveModelFunction(QualifiedName name, out ModelScalarFunction resolved)
    {
        resolved = null!;
        if (_modelCatalogResolver is null) return false;
        if (!string.Equals(name.Schema, "models", StringComparison.OrdinalIgnoreCase)) return false;

        ModelCatalog? catalog = _modelCatalogResolver();
        if (catalog is null) return false;

        if (catalog.TryGetEntry(name.Name) is null) return false;

        resolved = new ModelScalarFunction(name.Name, _modelCatalogResolver);
        return true;
    }

    /// <summary>
    /// Looks up the catalog descriptor for a scalar function by qualified
    /// name (or alias within the same schema).
    /// </summary>
    public FunctionDescriptor? TryGetScalarDescriptor(QualifiedName name)
    {
        _scalarDescriptorsByName.TryGetValue(name, out FunctionDescriptor? descriptor);
        return descriptor;
    }

    /// <summary>
    /// Back-compat descriptor lookup. See <see cref="TryGetScalar(string)"/>
    /// for the resolution rule.
    /// </summary>
    public FunctionDescriptor? TryGetScalarDescriptor(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetScalarDescriptor(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        foreach (string schema in DefaultSearchPath)
        {
            FunctionDescriptor? hit = TryGetScalarDescriptor(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    // ──────────────────── Other arity lookups ────────────────────

    /// <summary>Exact qualified lookup for a table-valued function.</summary>
    public ITableValuedFunction? TryGetTableValued(QualifiedName name)
    {
        _tableValuedFunctions.TryGetValue(name, out ITableValuedFunction? function);
        return function;
    }

    /// <summary>
    /// Back-compat bare-string lookup. Split-then-exact when dotted;
    /// otherwise walks <c>[public, system]</c>.
    /// </summary>
    public ITableValuedFunction? TryGetTableValued(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetTableValued(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        foreach (string schema in DefaultSearchPath)
        {
            ITableValuedFunction? hit = TryGetTableValued(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Exact qualified lookup for an aggregate function.</summary>
    public IAggregateFunction? TryGetAggregate(QualifiedName name)
    {
        _aggregateFunctions.TryGetValue(name, out IAggregateFunction? function);
        return function;
    }

    /// <summary>
    /// Back-compat bare-string lookup. Split-then-exact when dotted;
    /// otherwise walks <c>[public, system]</c>.
    /// </summary>
    public IAggregateFunction? TryGetAggregate(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetAggregate(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        foreach (string schema in DefaultSearchPath)
        {
            IAggregateFunction? hit = TryGetAggregate(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Exact qualified lookup for a dedicated window function.</summary>
    public IWindowFunction? TryGetWindow(QualifiedName name)
    {
        _windowFunctions.TryGetValue(name, out IWindowFunction? function);
        return function;
    }

    /// <summary>
    /// Back-compat bare-string lookup. Split-then-exact when dotted;
    /// otherwise walks <c>[public, system]</c>.
    /// </summary>
    public IWindowFunction? TryGetWindow(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetWindow(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        foreach (string schema in DefaultSearchPath)
        {
            IWindowFunction? hit = TryGetWindow(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Resolves a window function by name, checking the dedicated window registry
    /// first, then falling back to wrapping an aggregate function with
    /// <see cref="Window.AggregateWindowAdapter"/> if one exists in the same schema.
    /// </summary>
    public IWindowFunction? TryGetWindowOrAggregate(QualifiedName name)
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

    /// <summary>Back-compat bare-string overload. See <see cref="TryGetScalar(string)"/>.</summary>
    public IWindowFunction? TryGetWindowOrAggregate(string name)
    {
        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            return TryGetWindowOrAggregate(new QualifiedName(name[..dot], name[(dot + 1)..]));
        }
        foreach (string schema in DefaultSearchPath)
        {
            IWindowFunction? hit = TryGetWindowOrAggregate(new QualifiedName(schema, name));
            if (hit is not null) return hit;
        }
        return null;
    }

    // ──────────────────── Enumeration ────────────────────

    /// <summary>Every registered scalar function as <c>(schema, name)</c>.</summary>
    public IEnumerable<QualifiedName> ScalarFunctionQualifiedNames => _scalarFunctions.Keys;

    /// <summary>
    /// Bare names of every registered scalar (without schema). Preserves the
    /// pre-S7 enumeration shape for consumers that don't care about schema.
    /// </summary>
    public IEnumerable<string> ScalarFunctionNames => _scalarFunctions.Keys.Select(k => k.Name);

    /// <summary>
    /// Returns the descriptor for every primary scalar registration.
    /// Aliases are reported via <see cref="FunctionDescriptor.Aliases"/>.
    /// </summary>
    public IReadOnlyList<FunctionDescriptor> ScalarDescriptors => _scalarDescriptors;

    /// <summary>Every registered table-valued function as <c>(schema, name)</c>.</summary>
    public IEnumerable<QualifiedName> TableValuedFunctionQualifiedNames => _tableValuedFunctions.Keys;

    /// <summary>Bare names of every registered table-valued function.</summary>
    public IEnumerable<string> TableValuedFunctionNames => _tableValuedFunctions.Keys.Select(k => k.Name);

    /// <summary>Every registered aggregate function as <c>(schema, name)</c>.</summary>
    public IEnumerable<QualifiedName> AggregateFunctionQualifiedNames => _aggregateFunctions.Keys;

    /// <summary>Bare names of every registered aggregate function.</summary>
    public IEnumerable<string> AggregateFunctionNames => _aggregateFunctions.Keys.Select(k => k.Name);

    /// <summary>Every registered window function as <c>(schema, name)</c>.</summary>
    public IEnumerable<QualifiedName> WindowFunctionQualifiedNames => _windowFunctions.Keys;

    /// <summary>Bare names of every registered window function.</summary>
    public IEnumerable<string> WindowFunctionNames => _windowFunctions.Keys.Select(k => k.Name);

    /// <summary>
    /// Creates a registry pre-populated with all built-in functions. Every
    /// built-in lands in the <c>system</c> schema; the default
    /// <c>search_path</c> of <c>[public, system]</c> keeps unqualified
    /// calls resolving the same way they did before S7.
    /// </summary>
    public static FunctionRegistry CreateDefault()
    {
        FunctionRegistry registry = new();

        // ── Scalar ────────────────────────────────────────────────────────
        // System functions
        registry.RegisterScalar<Scalar.CastFunction>();
        registry.RegisterScalar<Scalar.TryCastFunction>();
        registry.RegisterScalar<Scalar.CanCastFunction>();
        registry.RegisterScalar<Scalar.TypeofFunction>();
        registry.RegisterScalar<Scalar.CoalesceFunction>();

        // String
        registry.RegisterScalar<Scalar.Strings.ConcatFunction>();
        registry.RegisterScalar<Scalar.Strings.ConcatStrictFunction>();
        registry.RegisterScalar<Scalar.Strings.ConcatWsFunction>();
        registry.RegisterScalar<Scalar.Strings.UpperFunction>();
        registry.RegisterScalar<Scalar.Strings.LowerFunction>();
        registry.RegisterScalar<Scalar.Strings.LenFunction>();

        // Fulltext family
        registry.RegisterScalar<Scalar.Fulltext.PlainToTsqueryFunction>();
        registry.RegisterScalar<Scalar.Fulltext.TsqueryMatchFunction>();

        // Assertion family — assert_not_null is the inliner-injected guard;
        // the rest are user-facing runtime checks that return the checked
        // value on success and throw on violation.
        registry.RegisterScalar<Scalar.Assertion.AssertNotNullFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertEqualFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertNotEqualFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertGreaterThanFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertGreaterOrEqualFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertLessThanFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertLessOrEqualFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertBetweenFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertTrueFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertFalseFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertFiniteFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertPositiveFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertNonNegativeFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertMatchesFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertStartsWithFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertEndsWithFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertNonEmptyFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertLengthFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertInFunction>();
        registry.RegisterScalar<Scalar.Assertion.AssertNotInFunction>();

        // Math/Numerics
        registry.RegisterScalar<Scalar.Math.AbsFunction>();
        registry.RegisterScalar<Scalar.Math.RoundFunction>();
        registry.RegisterScalar<Scalar.Math.FloorFunction>();
        registry.RegisterScalar<Scalar.Math.CeilFunction>();
        registry.RegisterScalarAlias<Scalar.Math.CeilFunction>("ceiling");
        registry.RegisterScalar<Scalar.Math.SqrtFunction>();

        registry.RegisterScalar<Scalar.RandomFunction>();
        registry.RegisterScalar<Scalar.HashSplitFunction>();

        // File
        registry.RegisterScalar<Scalar.File.GetFilenameFunction>();
        registry.RegisterScalar<Scalar.File.GetFilenameExtFunction>();
        registry.RegisterScalar<Scalar.File.GetDirectoryFunction>();
        registry.RegisterScalar<Scalar.File.PathConcatFunction>();
        registry.RegisterScalar<Scalar.File.GetFilenameNoExtFunction>();
        registry.RegisterScalar<Scalar.File.ChangeFilenameExtFunction>();
        registry.RegisterScalar<Scalar.File.ReadStringListFunction>();

        // Array
        registry.RegisterScalar<Scalar.Arrays.RandomChoiceFunction>();
        registry.RegisterScalar<Scalar.Arrays.ArrayConstructorFunction>();
        registry.RegisterScalar<Scalar.Arrays.ArrayToStringFunction>();
        registry.RegisterScalar<Scalar.Arrays.ArrayLengthFunction>();

        // UUID
        registry.RegisterScalar<Scalar.Uuid.UuidV4Function>();
        registry.RegisterScalarAlias<Scalar.Uuid.UuidV4Function>("gen_random_uuid");
        registry.RegisterScalar<Scalar.Uuid.UuidV7Function>();
        registry.RegisterScalar<Scalar.Uuid.UuidStrFunction>();
        registry.RegisterScalar<Scalar.Uuid.UuidExtractTimestampFunction>();
        registry.RegisterScalar<Scalar.Uuid.UuidExtractVersionFunction>();

        // Image
        registry.RegisterScalar<Scalar.Image.YoloxPreprocessFunction>();
        registry.RegisterScalar<Scalar.Image.YoloxPostprocessFunction>();
        registry.RegisterScalar<Scalar.Image.ImageToTensorChwBgrFunction>();
        registry.RegisterScalar<Scalar.Image.DepthMapToImageFunction>();
        registry.RegisterScalar<Scalar.Image.ImageCropFunction>();
        registry.RegisterScalar<Scalar.Image.ImageCutoutFunction>();
        registry.RegisterScalar<Scalar.Image.ImageDrawBoundingBoxesFunction>();
        registry.RegisterScalar<Scalar.Image.ApplyColormapFunction>();
        registry.RegisterScalar<Scalar.Image.ImageWidthFunction>();
        registry.RegisterScalar<Scalar.Image.ImageHeightFunction>();
        registry.RegisterScalar<Scalar.Image.ImageResizeToStrideFunction>();
        registry.RegisterScalar<Scalar.Image.ImageToTensorChwFunction>();
        registry.RegisterScalar<Scalar.Image.ImageToTensorHwcFunction>();
        registry.RegisterScalar<Scalar.Image.ImageLetterboxTensorChwFunction>();
        registry.RegisterScalar<Scalar.Image.ImageLetterboxTensorHwcFunction>();
        registry.RegisterScalar<Scalar.Image.TensorToImageChwFunction>();
        registry.RegisterScalar<Scalar.Image.TensorToImageHwcFunction>();
        registry.RegisterScalar<Scalar.Image.DbnetPostprocessFunction>();
        registry.RegisterScalar<Scalar.Image.ImagenetMeanFunction>();
        registry.RegisterScalar<Scalar.Image.ImagenetStdFunction>();
        registry.RegisterScalar<Scalar.Image.ClipMeanFunction>();
        registry.RegisterScalar<Scalar.Image.ClipStdFunction>();

        // Encoding
        registry.RegisterScalar<Scalar.Encoding.EncodeFunction>();
        registry.RegisterScalar<Scalar.Encoding.DecodeFunction>();

        // Crypto
        registry.RegisterScalar<Scalar.Crypto.Md5Function>();
        registry.RegisterScalar<Scalar.Crypto.Sha1Function>();
        registry.RegisterScalar<Scalar.Crypto.Sha256Function>();
        registry.RegisterScalar<Scalar.Crypto.Sha384Function>();
        registry.RegisterScalar<Scalar.Crypto.Sha512Function>();
        registry.RegisterScalar<Scalar.Crypto.DigestFunction>();

        // Activations — softmax / sigmoid. ReLU + GELU + tanh land when a
        // model actually needs them post-graph (most are baked into the
        // ONNX export).
        registry.RegisterScalar<Scalar.Activation.SoftmaxFunction>();
        registry.RegisterScalar<Scalar.Activation.SigmoidFunction>();
        registry.RegisterScalar<Scalar.Activation.MultilabelClassifyFunction>();

        // Vector reductions + normalization + detection postprocess.
        registry.RegisterScalar<Scalar.Vector.ArgmaxFunction>();
        registry.RegisterScalar<Scalar.Vector.TopkFunction>();
        registry.RegisterScalar<Scalar.Vector.L2NormalizeFunction>();
        registry.RegisterScalar<Scalar.Vector.MeanPoolMaskedFunction>();
        registry.RegisterScalar<Scalar.Vector.CosineSimilarityFunction>();
        registry.RegisterScalar<Scalar.Vector.NmsFunction>();
        registry.RegisterScalar<Scalar.Vector.MaskToPolygonFunction>();

        // Tokenization helpers live in their own `tokenizer` schema (sibling
        // to `inference` and `templates`) so the namespace is self-describing
        // and `decode` doesn't collide with the existing byte-encoding
        // system.decode. Two pairs: tokenizer.json form + classic
        // vocab.json+merges.txt form. See Functions/Tokenization/.
        registry.RegisterScalar<Tokenization.TokenizerEncodeFunction>("tokenizer");
        registry.RegisterScalar<Tokenization.TokenizerEncodeBpeFunction>("tokenizer");
        registry.RegisterScalar<Tokenization.TokenizerEncodeBertFunction>("tokenizer");
        registry.RegisterScalar<Tokenization.TokenizerEncodeRobertaFunction>("tokenizer");
        registry.RegisterScalar<Tokenization.TokenizerDecodeFunction>("tokenizer");
        registry.RegisterScalar<Tokenization.TokenizerDecodeBpeFunction>("tokenizer");

        // Temporal — current time, date/time arithmetic, extraction.
        registry.RegisterScalar<Scalar.Temporal.NowFunction>();
        registry.RegisterScalar<Scalar.Temporal.CyclicalEncodeFunction>();

        // Spatial — Point2D/Point3D construction, component access, distance.
        registry.RegisterScalar<Scalar.Spatial.Point2DFunction>();
        registry.RegisterScalar<Scalar.Spatial.Point3DFunction>();
        registry.RegisterScalar<Scalar.Spatial.PointXFunction>();
        registry.RegisterScalar<Scalar.Spatial.PointYFunction>();
        registry.RegisterScalar<Scalar.Spatial.PointZFunction>();
        registry.RegisterScalar<Scalar.Spatial.DistanceFunction>();
        registry.RegisterScalar<Scalar.Spatial.DistanceSqFunction>();

        // Json — parse, scalar lookup, subdocument query, text re-emit.
        // Backed by canonical CBOR in the arena; the codec lives in Functions/Json.
        registry.RegisterScalar<Scalar.Json.JsonParseFunction>();
        registry.RegisterScalar<Scalar.Json.JsonTryParseFunction>();
        registry.RegisterScalar<Scalar.Json.JsonValueFunction>();
        registry.RegisterScalar<Scalar.Json.JsonQueryFunction>();
        registry.RegisterScalar<Scalar.Json.JsonToTextFunction>();

        // ONNX
        registry.RegisterScalar<InferFunction>();

        // Templates — per-LLM-family chat-template primitives. Three
        // functions per family (open / msg / assistant_turn) for
        // assembling multi-turn prompts in plain SQL. See
        // Functions/Templates/ChatTemplateFunctions.cs for the family
        // list and call shape.
        Templates.ChatTemplateFunctions.RegisterAll(registry);

        // ── Table-valued ──────────────────────────────────────────────────
        // UNNEST retired pending the reference-type-array consolidation; will be
        // rebuilt on the new typed-array surface when a demand actually requires it.
        registry.RegisterTableValued<TableValued.RangeFunction>();

        // Inference toolkit. Lives in its own `inference` schema so the
        // introspection surface (onnx_inspect, devices, ...) doesn't
        // crowd `system`. Users call qualified: inference.onnx_inspect(...).
        registry.RegisterTableValued<TableValued.OnnxInspectFunction>("inference");
        registry.RegisterTableValued<TableValued.OnnxInspectMetaFunction>("inference");
        registry.RegisterTableValued<TableValued.InferCompatibilityFunction>("inference");
        registry.RegisterTableValued<TableValued.DevicesFunction>("inference");
        registry.RegisterScalar<Scalar.Inference.ModelSkeletonFunction>("inference");

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
