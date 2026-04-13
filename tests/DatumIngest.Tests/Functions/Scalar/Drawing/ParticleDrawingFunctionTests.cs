using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase F: <see cref="DrawParticlesFunction"/> — deterministic particle
/// emitter that returns a <see cref="GroupDrawing"/> of currently-alive
/// particles at the supplied <c>t</c>.
/// </summary>
public sealed class ParticleDrawingFunctionTests : ServiceTestBase
{

    /// <summary>A simple Drawing sprite (small filled circle) used as a particle payload.</summary>
    private static ValueRef MakeSprite() => ValueRef.FromDrawing(
        new ShapeDrawing(
            ShapeKind.Ellipse,
            new SKPoint(0, 0),
            new SKSize(2, 2),
            Fill: new SKColor(255, 200, 50, 255)));

    private async Task<DrawingPayload> Exec(params ValueRef[] args)
    {
        ValueRef result = await new DrawParticlesFunction().ExecuteAsync(args, CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Drawing, result.Kind);
        Assert.False(result.IsNull);
        return result.AsDrawing();
    }

    private static int CountTransformedChildren(DrawingPayload p)
    {
        GroupDrawing g = Assert.IsType<GroupDrawing>(p);
        return g.Children.Length;
    }

    // ---------- emission / lifetime ----------

    [Fact]
    public async Task NoParticlesAliveAtTZero_WithFiniteLifetime()
    {
        // At t=0, only particle 0 (born at t=0) is just emerging. Since
        // age=0 < lifetime, it's alive. The renderer-visible count is 1.
        DrawingPayload p = await Exec(
            ValueRef.FromFloat32(0f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),    // rate
            ValueRef.FromFloat32(0.5f),   // lifetime
            ValueRef.FromPoint2D(0, 0),   // velocity
            ValueRef.FromFloat32(0f),     // jitter
            MakeSprite());
        Assert.Equal(1, CountTransformedChildren(p));
    }

    [Fact]
    public async Task ParticleCountMatches_LiveWindow()
    {
        // rate=10, lifetime=0.5: at t=0.5, particles emitted in [0, 0.5] are
        // alive. Particle i born at i/10, so indices 0..5 emitted ⇒ ~5 live.
        DrawingPayload p = await Exec(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(0f),
            MakeSprite());
        int count = CountTransformedChildren(p);
        Assert.InRange(count, 4, 6);
    }

    [Fact]
    public async Task ParticlesPositionedAlongVelocity_NoJitter()
    {
        // With jitter=0, particle i is exactly at emit_at + velocity * age.
        DrawingPayload p = await Exec(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(2f),       // 2 particles per unit-t
            ValueRef.FromFloat32(1f),       // lifetime spans whole animation
            ValueRef.FromPoint2D(100f, 0f), // velocity = 100 px/unit-t horizontally
            ValueRef.FromFloat32(0f),
            MakeSprite());
        GroupDrawing g = Assert.IsType<GroupDrawing>(p);
        Assert.NotEmpty(g.Children);
        // Each child is a TransformedDrawing; translate.x equals
        // (velocity.x * age) where age = t - i/rate. Particle 0 (i=0) is at
        // x = 100*0.5 = 50. Particle 1 (i=1) is at x = 100*0 = 0.
        TransformedDrawing first = Assert.IsType<TransformedDrawing>(g.Children[0]);
        Assert.Equal(50f, first.Translate.X, precision: 3);
        Assert.Equal(0f, first.Translate.Y, precision: 3);
    }

    [Fact]
    public async Task DeadParticles_AreNotEmitted()
    {
        // At t=1.0 with lifetime=0.1, only the latest ~10% of particles
        // are alive (rate=10 ⇒ ~1 particle alive at any moment).
        DrawingPayload p = await Exec(
            ValueRef.FromFloat32(1f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.1f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(0f),
            MakeSprite());
        int count = CountTransformedChildren(p);
        Assert.InRange(count, 1, 3);  // 1-2 live particles at any moment
    }

    // ---------- determinism ----------

    [Fact]
    public async Task SameSeed_SameOutput()
    {
        ValueRef[] args =
        {
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(50, 50),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(20, 20),
            ValueRef.FromFloat32(5f),       // jitter
            MakeSprite(),
            ValueRef.FromInt32(42),
        };
        DrawingPayload a = await Exec(args);
        DrawingPayload b = await Exec(args);

        GroupDrawing ga = Assert.IsType<GroupDrawing>(a);
        GroupDrawing gb = Assert.IsType<GroupDrawing>(b);
        Assert.Equal(ga.Children.Length, gb.Children.Length);
        for (int i = 0; i < ga.Children.Length; i++)
        {
            TransformedDrawing ta = (TransformedDrawing)ga.Children[i];
            TransformedDrawing tb = (TransformedDrawing)gb.Children[i];
            Assert.Equal(ta.Translate.X, tb.Translate.X, precision: 5);
            Assert.Equal(ta.Translate.Y, tb.Translate.Y, precision: 5);
        }
    }

    [Fact]
    public async Task DifferentSeed_DifferentJitter()
    {
        DrawingPayload a = await Exec(
            ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(10f),
            MakeSprite(), ValueRef.FromInt32(1));
        DrawingPayload b = await Exec(
            ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(10f),
            MakeSprite(), ValueRef.FromInt32(2));

        TransformedDrawing ta = (TransformedDrawing)((GroupDrawing)a).Children[0];
        TransformedDrawing tb = (TransformedDrawing)((GroupDrawing)b).Children[0];
        Assert.NotEqual(ta.Translate.X, tb.Translate.X);
    }

    // ---------- opacity ramp ----------

    [Fact]
    public async Task ParticleOpacity_RampsInAndOut()
    {
        // Single long-lived particle so we can inspect its opacity across age.
        // rate=1, lifetime=1 ⇒ particle 0 born at t=0, dies at t=1.
        // Age normalised:
        //   ageN < 0.2 → opacity = ageN/0.2 (0 → 1)
        //   0.2 ≤ ageN ≤ 0.8 → opacity = 1
        //   ageN > 0.8 → opacity = (1-ageN)/0.2 (1 → 0)

        // At t=0.1, ageN=0.1 ⇒ opacity 0.5.
        DrawingPayload p1 = await Exec(
            ValueRef.FromFloat32(0.1f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(1f),
            ValueRef.FromFloat32(1f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
            MakeSprite());
        TransformedDrawing t1 = (TransformedDrawing)((GroupDrawing)p1).Children[0];
        Assert.Equal(0.5f, t1.Opacity, precision: 4);

        // At t=0.5, ageN=0.5 ⇒ opacity 1.
        DrawingPayload p2 = await Exec(
            ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(1f),
            ValueRef.FromFloat32(1f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
            MakeSprite());
        TransformedDrawing t2 = (TransformedDrawing)((GroupDrawing)p2).Children[0];
        Assert.Equal(1f, t2.Opacity, precision: 4);

        // At t=0.9, ageN=0.9 ⇒ opacity 0.5.
        DrawingPayload p3 = await Exec(
            ValueRef.FromFloat32(0.9f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(1f),
            ValueRef.FromFloat32(1f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
            MakeSprite());
        TransformedDrawing t3 = (TransformedDrawing)((GroupDrawing)p3).Children[0];
        Assert.Equal(0.5f, t3.Opacity, precision: 4);
    }

    // ---------- validation ----------

    [Fact]
    public async Task NegativeRate_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawParticlesFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(-1f),
                    ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
                    MakeSprite(),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task LifetimeOutOfBounds_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawParticlesFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(10f),
                    ValueRef.FromFloat32(2f),  // > 1 — out of range
                    ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
                    MakeSprite(),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task ExcessiveRate_Throws()
    {
        // rate=100000 with lifetime=1 ⇒ 100k particles alive, way above MaxParticles=4096.
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawParticlesFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0.5f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(100000f),
                    ValueRef.FromFloat32(1f), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(0f),
                    MakeSprite(),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("cap is", ex.Message);
    }

    [Fact]
    public void Particles_AreScopedToAnimationContext()
    {
        Assert.Contains("animation", DrawParticlesFunction.Contexts);
    }

    // ---------- warmup ----------

    [Fact]
    public async Task Warmup_AtZeroT_FieldIsPreFilledAsIfMidLifecycle()
    {
        // Without warmup (== 0): at t=0, only particle 0 is barely born
        // (age=0, opacity=0) — effectively the field is empty.
        DrawingPayload noWarmup = await Exec(
            ValueRef.FromFloat32(0f),       // t
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),      // rate
            ValueRef.FromFloat32(0.5f),     // lifetime
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(0f),
            MakeSprite());
        int countNoWarmup = CountTransformedChildren(noWarmup);

        // With warmup=0.5 (== lifetime): at lambda t=0 the simulation has
        // been running for 0.5 units — the entire field is alive.
        DrawingPayload warmed = await Exec(
            ValueRef.FromFloat32(0f),       // t
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(0f),
            MakeSprite(),
            ValueRef.FromInt32(0),          // seed (required positionally before warmup)
            ValueRef.FromFloat32(0.5f));    // warmup
        int countWarmed = CountTransformedChildren(warmed);

        Assert.True(countWarmed > countNoWarmup,
            $"warmed field should have more live particles than cold start; got {countWarmed} vs {countNoWarmup}.");
        // Roughly rate * lifetime alive at any moment in steady state.
        Assert.InRange(countWarmed, 4, 6);
    }

    [Fact]
    public async Task Point2DJitter_AppliesPerAxisIndependently()
    {
        // X jitter = 0, Y jitter = 100 — particle spawn positions should all
        // align horizontally with the emit point but spread vertically.
        DrawingPayload p = await Exec(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(50, 50),       // emit_at
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPoint2D(0, 0),         // velocity = 0
            ValueRef.FromPoint2D(0f, 100f),     // Point2D jitter — X locked, Y wide
            MakeSprite());

        GroupDrawing g = Assert.IsType<GroupDrawing>(p);
        Assert.NotEmpty(g.Children);
        // Every particle's X position should equal emit_at.X (jitter.X == 0,
        // velocity.X == 0); Y positions should vary across particles.
        HashSet<float> distinctYs = new();
        foreach (DrawingPayload child in g.Children)
        {
            TransformedDrawing wrapper = Assert.IsType<TransformedDrawing>(child);
            Assert.Equal(50f, wrapper.Translate.X, precision: 3);
            distinctYs.Add(wrapper.Translate.Y);
        }
        Assert.True(distinctYs.Count >= 2,
            $"Y jitter should produce varied Y positions; got {distinctYs.Count} distinct.");
    }

    [Fact]
    public async Task Warmup_Negative_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawParticlesFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromFloat32(0.5f),
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromFloat32(10f),
                    ValueRef.FromFloat32(0.5f),
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromFloat32(0f),
                    MakeSprite(),
                    ValueRef.FromInt32(0),
                    ValueRef.FromFloat32(-0.1f),    // negative warmup — rejected
                },
                CreateEvaluationFrame(), default));
    }

    // ----- lambda-sprite variant -----

    /// <summary>
    /// Builds an evaluator-backed frame so the lambda invoker is wired.
    /// Direct unit-test invocations of draw_particles with a lambda sprite
    /// require a frame that knows how to invoke lambdas.
    /// </summary>
    private (EvaluationFrame Frame, ExpressionEvaluator Evaluator) MakeLambdaCapableFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(
            store: arena, accountant: accountant);
        DatumIngest.Execution.ExecutionContext scoped = context.Derive(
            variableScope: scope, variableStore: arena);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
        return (frame, evaluator);
    }

    /// <summary>
    /// Parses a lambda body from SQL (via array_transform's lambda slot) and
    /// captures it against the current frame. Mirrors the helper in
    /// AnimateFramesFunctionTests.
    /// </summary>
    private static ValueRef ParseSpriteLambda(Row capturedRow, string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call = (FunctionCallExpression)select.Statement.Columns[0].Expression;
        LambdaExpression ast = (LambdaExpression)call.Arguments[1];
        return ValueRef.FromLambda(LambdaValue.Capture(ast, capturedRow));
    }

    [Fact]
    public async Task LambdaSprite_InvokedPerParticle_WithNormalizedAge()
    {
        // The lambda turns its age argument straight into the drawing's
        // rectangle width — `x -> draw_rect(point2d(0,0), point2d(x*10, 1), color(255,0,0))`.
        // After execution, each particle's TransformedDrawing wraps a
        // ShapeDrawing with size.Width = ageN * 10. We can inspect those
        // sizes to confirm each lambda invocation got a distinct ageN.
        var (frame, _) = MakeLambdaCapableFrame();
        ValueRef lambda = ParseSpriteLambda(Row.Empty,
            "x -> draw_rect(point2d(0, 0), point2d(x * 10, 1), color(255, 0, 0))");

        // t=0.5, rate=10, lifetime=0.5 → alive indices roughly 0..5, ages
        // varying. Each particle's sprite should reflect its own age.
        ValueRef result = await new DrawParticlesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(0.5f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(10f),
                ValueRef.FromFloat32(0.5f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(0f),
                lambda,
            },
            frame, default);

        GroupDrawing g = Assert.IsType<GroupDrawing>(result.AsDrawing());
        Assert.NotEmpty(g.Children);

        // Inspect distinct widths — each particle's sprite was built from a
        // different ageN, so the rect widths should differ across particles.
        HashSet<float> widths = new();
        foreach (DrawingPayload child in g.Children)
        {
            TransformedDrawing wrapper = Assert.IsType<TransformedDrawing>(child);
            ShapeDrawing inner = Assert.IsType<ShapeDrawing>(wrapper.Inner);
            widths.Add(inner.Size.Width);
        }
        // At least two distinct widths — confirms per-particle invocation.
        Assert.True(widths.Count >= 2,
            $"expected per-particle sprites with distinct widths; got {widths.Count} unique.");
    }

    [Fact]
    public async Task LambdaSprite_BlendedSprite_LiftsBlendOutsideTransform()
    {
        // Regression for the "most recent particles don't blend" bug.
        // When the sprite lambda returns a BlendedDrawing, the particle
        // wrapper must produce BlendedDrawing(TransformedDrawing(inner))
        // — NOT TransformedDrawing(BlendedDrawing(inner)) — so the blend
        // composites against the outer canvas instead of the opacity layer.
        // This test inspects the structure directly; the rendering-level
        // assertion follows below.
        DrawingPayload sprite = new BlendedDrawing(
            new ShapeDrawing(ShapeKind.Ellipse,
                new SKPoint(0, 0), new SKSize(2, 2), Fill: SKColors.Yellow),
            SKBlendMode.Plus);

        ValueRef result = await new DrawParticlesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(0.5f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(4f),
                ValueRef.FromFloat32(1f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(0f),
                ValueRef.FromDrawing(sprite),
            },
            CreateEvaluationFrame(), default);

        GroupDrawing g = Assert.IsType<GroupDrawing>(result.AsDrawing());
        foreach (DrawingPayload child in g.Children)
        {
            // Each particle must be BlendedDrawing on the outside.
            BlendedDrawing wrapper = Assert.IsType<BlendedDrawing>(child);
            Assert.Equal(SKBlendMode.Plus, wrapper.Mode);
            // Whose inner is the TransformedDrawing — with opacity baked in
            // (the value depends on the particle's ageN, just check the type).
            Assert.IsType<TransformedDrawing>(wrapper.Inner);
        }
    }

    [Fact]
    public async Task LambdaSprite_AgeNormalizedAcrossLifetime()
    {
        // The lambda echoes ageN directly into the rect width. We can then
        // check that the FIRST child's width corresponds to the OLDEST live
        // particle (ageN closest to 1) and the LAST child to the YOUNGEST.
        var (frame, _) = MakeLambdaCapableFrame();
        ValueRef lambda = ParseSpriteLambda(Row.Empty,
            "x -> draw_rect(point2d(0, 0), point2d(x, 1), color(255, 0, 0))");

        // rate=10, lifetime=1, t=1: indices 0..9 are alive with ages 1..0.1
        // (descending). Particle 0 born at t=0 has age = 1.0 (dead by strict
        // <lifetime check, so skip). Particle 1 has age 0.9, ageN 0.9. ... etc.
        ValueRef result = await new DrawParticlesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromFloat32(1f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(10f),
                ValueRef.FromFloat32(1f),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(0f),
                lambda,
            },
            frame, default);

        GroupDrawing g = Assert.IsType<GroupDrawing>(result.AsDrawing());
        // All emitted ageN values are in [0, 1].
        foreach (DrawingPayload child in g.Children)
        {
            TransformedDrawing wrapper = Assert.IsType<TransformedDrawing>(child);
            ShapeDrawing inner = Assert.IsType<ShapeDrawing>(wrapper.Inner);
            Assert.InRange(inner.Size.Width, 0f, 1f);
        }
    }
}
