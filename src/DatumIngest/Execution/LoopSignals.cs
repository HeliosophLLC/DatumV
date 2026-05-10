namespace DatumIngest.Execution;

/// <summary>
/// Internal control-flow signal raised by <c>BREAK</c> inside a
/// procedural loop. Caught by the innermost enclosing loop's
/// per-iteration wrapper. If it escapes all loops the batch / procedure
/// entry points convert it to a clear
/// <see cref="InvalidOperationException"/>. Singleton for zero
/// allocation.
/// </summary>
internal sealed class LoopBreakSignal : Exception
{
    public static readonly LoopBreakSignal Instance = new();
    private LoopBreakSignal() : base("BREAK outside of a loop.") { }
}

/// <summary>
/// Internal control-flow signal raised by <c>CONTINUE</c> inside a
/// procedural loop. Caught by the innermost enclosing loop's
/// per-iteration wrapper. If it escapes all loops the batch / procedure
/// entry points convert it to a clear
/// <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class LoopContinueSignal : Exception
{
    public static readonly LoopContinueSignal Instance = new();
    private LoopContinueSignal() : base("CONTINUE outside of a loop.") { }
}
