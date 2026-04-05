using System.Collections.Immutable;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Execution.Contexts;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// <c>draw_particles(t, emit_at, rate, lifetime, velocity, jitter, sprite)</c> → Drawing.
/// <c>draw_particles(t, emit_at, rate, lifetime, velocity, jitter, sprite, seed)</c> → Drawing.
/// Emits deterministic particles from <c>emit_at</c> over time, returning
/// the set of currently-alive particles as a composited Drawing at the
/// current frame's <c>t</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Determinism.</strong> Each particle's emission time, position
/// jitter, and velocity jitter are derived from <c>(seed, particle_index)</c>
/// via the same hash used by <c>random_walk</c> and <c>wobble</c>. This
/// means the same call site evaluated at the same <c>t</c> always produces
/// the same particle field — required so that successive frames stack
/// coherently into an animation rather than re-rolling each frame.
/// </para>
/// <para>
/// <strong>Simulation model.</strong> Particles emit at a steady rate
/// <c>rate</c> (particles per unit-<c>t</c>) starting at <c>t = 0</c>.
/// Particle <c>i</c> is born at <c>t = i / rate</c>, lives for
/// <c>lifetime</c>, and travels at <c>velocity</c> from its (jittered)
/// spawn point. At any frame the function iterates particle indices
/// <c>0 .. ⌈rate⌉</c>, keeps the ones currently alive, and emits each
/// as a transformed copy of the <c>sprite</c> Drawing positioned at its
/// current location.
/// </para>
/// <para>
/// <strong>Fade.</strong> Each particle's opacity ramps from 0 over the
/// first 20% of its lifetime, holds at 1 through the middle 60%, then
/// ramps to 0 over the last 20%. Hides the pop-in/pop-out at birth/death
/// boundaries without needing the user to compose a fade curve manually.
/// </para>
/// <para>
/// <strong>Sprite ownership.</strong> The sprite Drawing is row-scoped
/// (per the Lambda DataKind rule); we just hold the same payload tree
/// inside each <see cref="TransformedDrawing"/> wrapper. The render
/// pipeline doesn't mutate Drawing payloads, so sharing is safe.
/// </para>
/// </remarks>
public sealed class DrawParticlesFunction : IFunction, IScalarFunction
{
    /// <summary>
    /// Cap on the number of particles considered per call. Hard limit
    /// rather than a soft warning — animations that hit this limit
    /// almost certainly have a mistaken <c>rate</c> argument and need to
    /// know about it.
    /// </summary>
    private const int MaxParticles = 4096;

    /// <inheritdoc />
    public static string Name => "draw_particles";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Deterministic particle emitter. Emits rate particles per unit-t from emit_at "
        + "with the given velocity and per-particle position/velocity jitter; "
        + "each particle renders the supplied sprite Drawing for its lifetime "
        + "with an automatic in/out opacity ramp.";

    /// <inheritdoc />
    public static IReadOnlyList<string> Contexts => [AnimationContext.Name];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Static-sprite variants — single Drawing reused for every particle.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("seed",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // ... + warmup: the simulation is offset so at lambda's t=0 it's
        // already mid-flow. Mostly useful for animations that would
        // otherwise loop visibly empty for the first ~lifetime frames.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("seed",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("warmup",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Lambda-sprite variants — sprite_fn(x: Float32) → Drawing called once per
        // alive particle with x = age/lifetime. Lets the sprite vary with the
        // particle's life — colour shift, size shrink, rotation, etc.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
                new ParameterSpec("seed",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // ... + warmup, same role as the static-sprite variant above.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
                new ParameterSpec("seed",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("warmup",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),

        // ────────── Point2D-jitter mirrors of the 6 variants above ──────────
        // Passing `jitter` as a Point2D lets the user scale the X and Y axes
        // of the per-particle random offset independently — useful when you
        // want, say, a tall narrow fountain (low X jitter, high Y jitter) or
        // a wide flat plume. Scalar `jitter` is preserved for back-compat and
        // because most callers want symmetric jitter.

        // Static sprite, Point2D jitter, no extras.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Static sprite, Point2D jitter, + seed.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("seed",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Static sprite, Point2D jitter, + seed, + warmup.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("seed",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("warmup",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Lambda sprite, Point2D jitter, no extras.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Lambda sprite, Point2D jitter, + seed.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
                new ParameterSpec("seed",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        // Lambda sprite, Point2D jitter, + seed, + warmup.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("t",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("emit_at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("rate",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("lifetime",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("velocity",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("jitter",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("sprite_fn", DataKindMatcher.Lambda(
                                                   ParticleContext.Name,
                                                   DataKindMatcher.Exact(DataKind.Drawing))),
                new ParameterSpec("seed",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("warmup",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawParticlesFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        // Snapshot the args off the Span before any awaits — ReadOnlySpan can't
        // cross await boundaries (the lambda variant awaits the invoker).
        float t;
        Vector2 emitAt;
        float rate;
        float lifetime;
        Vector2 velocity;
        // Jitter resolves to a per-axis (X, Y) pair regardless of overload —
        // the scalar form fills both components with the same value, the
        // Point2D form keeps them independent.
        Vector2 jitter;
        bool spriteIsLambda;
        DrawingPayload? staticSprite = null;
        ValueRef spriteLambda = default;
        int seed;
        float warmup;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].IsNull) return ValueRef.Null(DataKind.Drawing);
            }
            t = args[0].ToFloat();
            emitAt = args[1].AsPoint2D();
            rate = args[2].ToFloat();
            lifetime = args[3].ToFloat();
            velocity = args[4].AsPoint2D();
            // Scalar jitter → symmetric (X = Y). Point2D jitter → independent
            // per-axis. The signature variants ensure args[5] is either
            // Float32-family or Point2D — no other kind survives validation.
            jitter = args[5].Kind == DataKind.Point2D
                ? args[5].AsPoint2D()
                : new Vector2(args[5].ToFloat(), args[5].ToFloat());
            spriteIsLambda = args[6].Kind == DataKind.Lambda;
            if (spriteIsLambda)
            {
                spriteLambda = args[6];
            }
            else
            {
                staticSprite = args[6].AsDrawing();
            }
            seed = args.Length >= 8 ? args[7].ToInt32() : 0;
            warmup = args.Length >= 9 ? args[8].ToFloat() : 0f;
        }

        if (rate <= 0)
        {
            throw new FunctionArgumentException(Name, $"rate must be positive; got {rate}.");
        }
        if (lifetime <= 0 || lifetime > 1)
        {
            throw new FunctionArgumentException(Name,
                $"lifetime must be in (0, 1]; got {lifetime}.");
        }
        if (warmup < 0)
        {
            throw new FunctionArgumentException(Name,
                $"warmup must be non-negative; got {warmup}.");
        }
        if (spriteIsLambda && frame.LambdaInvoker is null)
        {
            throw new InvalidOperationException(
                "draw_particles with a lambda sprite_fn requires an ILambdaInvoker on the "
                + "evaluation frame. The query pipeline auto-attaches one via "
                + "ExpressionEvaluator; this error indicates a frame built outside that pipeline.");
        }

        // Iterate every particle that could be alive at the effective time
        // `tEff = t + warmup`. Particle i was born at i/rate measured against
        // tEff, so we consider indices i where (tEff - lifetime) < i/rate ≤ tEff.
        // lastIdx is exclusive: floor(tEff*rate) is the highest index that's
        // been born, so the loop runs `i < floor(tEff*rate) + 1`. Without the
        // +1 we'd skip particle 0 at t=0 (its emit_time and t are both 0 →
        // still emitted with opacity 0).
        //
        // Warmup shifts the simulation forward: at the lambda's t=0 the field
        // already contains particles emitted during [0, warmup] at various
        // points in their lifecycle, so animations don't start visibly empty.
        float tEff = t + warmup;
        int firstIdx = System.Math.Max(0, (int)MathF.Floor((tEff - lifetime) * rate));
        int lastIdx = (int)MathF.Floor(tEff * rate) + 1;
        if (lastIdx - firstIdx > MaxParticles)
        {
            throw new FunctionArgumentException(Name,
                $"draw_particles would consider {lastIdx - firstIdx} live particles at t={t} "
                + $"(rate={rate}, lifetime={lifetime}, warmup={warmup}); cap is {MaxParticles}. "
                + "Reduce rate, lifetime, or warmup.");
        }

        ImmutableArray<DrawingPayload>.Builder children = ImmutableArray.CreateBuilder<DrawingPayload>();
        ValueRef[]? lambdaArgs = spriteIsLambda ? new ValueRef[1] : null;
        for (int i = firstIdx; i < lastIdx; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float emitTime = i / rate;
            float age = tEff - emitTime;
            if (age < 0f || age >= lifetime)
            {
                continue;
            }

            // Per-particle deterministic jitter: 4 independent samples in [-1, 1].
            float jx = SymmetricJitter(seed, i, 0);
            float jy = SymmetricJitter(seed, i, 1);
            float jvx = SymmetricJitter(seed, i, 2);
            float jvy = SymmetricJitter(seed, i, 3);

            float spawnX = emitAt.X + jitter.X * jx;
            float spawnY = emitAt.Y + jitter.Y * jy;
            float dx = (velocity.X + jitter.X * jvx) * age;
            float dy = (velocity.Y + jitter.Y * jvy) * age;

            // Auto opacity: 0 → 1 over first 20% of lifetime, hold at 1,
            // 1 → 0 over last 20%. age is in [0, lifetime), normalise first.
            float ageN = age / lifetime;
            float opacity = ageN < 0.2f
                ? ageN / 0.2f
                : ageN > 0.8f
                    ? (1f - ageN) / 0.2f
                    : 1f;

            DrawingPayload spriteForThisParticle;
            if (spriteIsLambda)
            {
                lambdaArgs![0] = ValueRef.FromFloat32(ageN);
                ValueRef result = await frame.LambdaInvoker!.InvokeLambdaAsync(
                    spriteLambda, lambdaArgs, frame, cancellationToken).ConfigureAwait(false);
                if (result.IsNull)
                {
                    // Null Drawing → skip this particle. Matches the "CASE may
                    // emit null" tolerance of frames_to_gif.
                    continue;
                }
                spriteForThisParticle = result.AsDrawing();
            }
            else
            {
                spriteForThisParticle = staticSprite!;
            }

            // Layer ordering when the sprite carries a blend mode: the blend
            // wrapper must end up OUTSIDE the transform/opacity layer, not
            // inside it. Otherwise the blend composites against the (fresh,
            // transparent) opacity layer — a no-op — instead of the actual
            // canvas. Visible symptom: particles in fade-in/fade-out
            // (opacity < 1) don't blend with the rest of the scene, while
            // hold-period particles (opacity == 1, no opacity layer) do.
            DrawingPayload inner = spriteForThisParticle;
            SKBlendMode? wrapperBlend = null;
            if (spriteForThisParticle is BlendedDrawing bd)
            {
                inner = bd.Inner;
                wrapperBlend = bd.Mode;
            }
            DrawingPayload positioned = new TransformedDrawing(
                Inner: inner,
                Anchor: SKPoint.Empty,
                Translate: new SKPoint(spawnX + dx, spawnY + dy),
                Scale: 1f,
                RotationDegrees: 0f,
                Opacity: opacity);
            DrawingPayload child = wrapperBlend is { } mode
                ? new BlendedDrawing(positioned, mode)
                : positioned;
            children.Add(child);
        }

        DrawingPayload group = children.Count == 0
            // Always return a Drawing — null would be propagation poison.
            ? new GroupDrawing(ImmutableArray<DrawingPayload>.Empty)
            : new GroupDrawing(children.ToImmutable());
        return ValueRef.FromDrawing(group);
    }

    /// <summary>
    /// Deterministic per-particle jitter in <c>[-1, 1]</c>. Built atop the
    /// same hash mix used elsewhere in animation curves — three integer
    /// inputs (seed, particle index, component) give us four independent
    /// samples per particle for spawn-position + velocity perturbation.
    /// </summary>
    private static float SymmetricJitter(int seed, int particle, int component)
    {
        // Mix all three integers into a single seed for the helper. Multiplier
        // primes give independence across the (particle, component) plane —
        // walking either dimension produces uncorrelated outputs.
        int mixedSeed = unchecked(seed * (int)0x9E3779B1 + component * 0x27D4EB2D);
        float u = AnimationCurveHelpers.HashToUnitFloat(mixedSeed, particle);
        return u * 2f - 1f;
    }
}
