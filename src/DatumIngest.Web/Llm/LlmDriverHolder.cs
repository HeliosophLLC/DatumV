namespace DatumIngest.Web.Llm;

// Mutable holder for the singleton ILlmDriver. Set by LlmStartupService
// during IHostedService.StartAsync; read by any service that depends on
// the LLM (IConversationAgent, eventually IProactiveAgent). Two-stage
// registration like this avoids the alternatives:
//  - A DI factory that performs the eager load synchronously would block
//    every Resolve until the model is loaded *and* couldn't surface
//    cancellation cleanly. Async factories aren't a thing in built-in DI.
//  - A Lazy<Task<ILlmDriver>> would force every consumer to await even
//    after the load completes, which leaks the async-init concern into
//    every call site.
//
// Setting is one-shot; the holder doesn't support model swap at runtime.
// When model switching arrives, the holder grows a CompareAndSwap shape.
internal sealed class LlmDriverHolder
{
    private ILlmDriver? _current;

    public ILlmDriver Current => _current
        ?? throw new InvalidOperationException(
            "LLM driver not initialised yet. LlmStartupService.StartAsync must run before any " +
            "consumer resolves ILlmDriver. If you're seeing this during app startup, check " +
            "the hosted-service order — LlmStartupService should run before the agent's first use.");

    public void Set(ILlmDriver driver)
    {
        if (_current is not null)
        {
            throw new InvalidOperationException(
                "LLM driver already set. Runtime model swap is not supported in v1.");
        }
        _current = driver;
    }
}
