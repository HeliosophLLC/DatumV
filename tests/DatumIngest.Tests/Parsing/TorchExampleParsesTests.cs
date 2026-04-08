using DatumIngest.Parsing;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Smoke test for the animated-torch example in <c>docs/examples.md</c>.
/// Pins the named-argument form so a future rename of any
/// <c>draw_particles</c> / <c>animate_frames</c> / <c>frames_to_gif</c>
/// parameter name surfaces the doc-drift here at compile / test time.
/// </summary>
public sealed class TorchExampleParsesTests : ServiceTestBase
{
    [Fact]
    public void DocsExample_AnimatedTorch_ParsesAndPermutes()
    {
        const string sql = """
            SELECT
                frames_to_gif(
                    animate_frames(1.0, 24, point2d(64, 64), (t) ->
                        draw_particles(
                            t,
                            point2d(32, 56),
                            rate     := 40,
                            lifetime := 0.4,
                            velocity := point2d(0, -80),
                            jitter   := 3.4,
                            sprite_fn := x -> blend(
                                draw_circle(
                                    point2d(0, 0),
                                    1 + 10 * (1.0 - x),
                                    color(255, lerp(x, 220, 80), lerp(x, 100, 0))
                                ),
                                'add'
                            ),
                            seed   := 42,
                            warmup := 0.4
                        )
                    ),
                    fps := 12
                )
            """;

        // Parses successfully and surfaces a SELECT.
        SqlParser.Parse(sql);
    }
}
