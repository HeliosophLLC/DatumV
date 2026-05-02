namespace DatumIngest.Tests.Models.Llama;

using System.Text;

using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Llama;
using DatumIngest.Pooling;

/// <summary>
/// Phase B end-to-end tests for the LlamaSharp-backed LLM.
/// </summary>
/// <remarks>
/// <para>
/// These tests touch a real GGUF model file at the user's local
/// <see cref="ModelCatalog.DefaultModelDirectory"/>. They self-skip when the file
/// is absent so CI machines without the artefact don't fail. Locally, drop
/// <c>Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf</c> from
/// <a href="https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF">bartowski's
/// repo</a> into <c>E:\models</c> to enable them.
/// </para>
/// <para>
/// Each test loads the full model (~5 GB into VRAM); this is slow and memory-
/// intensive by design. Generation tokens are capped (<c>maxTokens=64</c>) so
/// each test finishes in seconds rather than minutes.
/// </para>
/// </remarks>
[Trait("Category", "Gpu")]
public sealed class LlamaModelTests : ServiceTestBase
{
    static LlamaModelTests()
    {
        // Tests run on whatever backend the system can provide. In production
        // (the shell) we require CUDA so the user gets a real error if it's
        // misconfigured; here we want tests green on CPU-only machines too.
        LlamaNativeConfig.RequireCuda = false;
    }

    // Catalog-substrate-aware path: SQL-installed weights live at
    // <DATUM_MODELS>/<catalog-id>/<active-version>/<file>. The 2026-06-01
    // version is the current cut of the llama-3.1-8b-instruct-gguf
    // catalog entry; update this constant when the entry is bumped.
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory,
        "llama-3.1-8b-instruct-gguf",
        "2026-06-01",
        "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf");

    private static bool ModelAvailable => File.Exists(ModelPath);

    /// <summary>
    /// Loading the GGUF and exposing the declared signature should succeed
    /// when the file exists. Cheapest signal that the LlamaSharp wiring + CUDA
    /// backend resolve correctly on this machine.
    /// </summary>
    [Fact]
    public void Load_RealLlama31_ExposesExpectedSignature()
    {
        if (!ModelAvailable)
        {
            return;
        }

        using LlamaModel model = new(name: "llama31_8b", modelFilePath: ModelPath, maxTokens: 16);

        Assert.Equal("llama31_8b", model.Name);
        Assert.False(model.IsDeterministic);
        Assert.Equal(DataKind.String, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.String, model.InputKinds[0]);
    }

    /// <summary>
    /// One-shot inference: send a simple prompt, expect a non-empty string back.
    /// Doesn't pin the exact response (LLM is nondeterministic), only that
    /// generation completed and produced something usable.
    /// </summary>
    [Fact]
    public async Task InferBatch_SimplePrompt_ReturnsNonEmptyResponse()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using LlamaModel model = new(name: "llama31_8b", modelFilePath: ModelPath, maxTokens: 32);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromString("What is 2 + 2? Reply with just the number.")],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef response = Assert.Single(outputs);
            Assert.False(response.IsNull);
            string text = response.AsString();
            Assert.False(string.IsNullOrWhiteSpace(text), "LLM returned empty response");
            // Stop-token leak is the most common failure mode — explicitly catch it.
            Assert.DoesNotContain("<|eot_id|>", text);
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Multi-row dispatch: confirms each row gets its own response and the
    /// stateless executor doesn't bleed context between rows. Each prompt asks
    /// for a unique token so we can verify they're not duplicates of each other.
    /// </summary>
    [Fact]
    public async Task InferBatch_MultipleRows_ReturnsOneResponsePerRow()
    {
        if (!ModelAvailable)
        {
            return;
        }

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using LlamaModel model = new(name: "llama31_8b", modelFilePath: ModelPath, maxTokens: 24);

            DatumIngest.Functions.ValueRef[][] inputs =
            [
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'apple'.")],
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'banana'.")],
                [DatumIngest.Functions.ValueRef.FromString("Reply with only the word 'cherry'.")],
            ];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs,
                overrides: [],
                cancellationToken: CancellationToken.None);

            Assert.Equal(3, outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                DatumIngest.Functions.ValueRef response = outputs[i];
                Assert.False(response.IsNull, $"row {i} returned a null response");
                string text = response.AsString();
                Assert.False(string.IsNullOrWhiteSpace(text), $"row {i} returned empty");
                Assert.DoesNotContain("<|eot_id|>", text);
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// Streaming inference: <c>InferStreamingAsync</c> drains cleanly against
    /// the real model and produces a non-empty, stop-token-free concatenation.
    /// The deterministic per-chunk-ordering invariant is covered by
    /// <c>ModelStreamingTests</c> against a synthetic backend; here the only
    /// thing worth pinning is "the streaming method actually runs end-to-end
    /// with LlamaSharp" without depending on response length.
    /// </summary>
    [Fact]
    public async Task InferStreaming_RealModel_DrainsToNonEmptyOutput()
    {
        if (!ModelAvailable)
        {
            return;
        }

        using LlamaModel model = new(name: "llama31_8b", modelFilePath: ModelPath, maxTokens: 64);

        DatumIngest.Functions.ValueRef[] rowInputs =
        [
            DatumIngest.Functions.ValueRef.FromString("Count from 1 to 5, one number per line."),
        ];

        StringBuilder concatenated = new();
        int chunkCount = 0;
        await foreach (DatumIngest.Functions.ValueRef chunk in model.InferStreamingAsync(
            rowInputs, rowOverrides: [], CancellationToken.None))
        {
            Assert.False(chunk.IsNull, $"chunk {chunkCount} was null");
            concatenated.Append(chunk.AsString());
            chunkCount++;
        }

        Assert.True(chunkCount >= 1, "stream yielded zero chunks");
        string full = concatenated.ToString();
        Assert.False(string.IsNullOrWhiteSpace(full), "concatenated stream was empty");
        Assert.DoesNotContain("<|eot_id|>", full);
    }

    /// <summary>
    /// Catalog round-trip: registering the model via <see cref="BuiltinModels"/>
    /// and resolving it through <see cref="ModelCatalog.GetModel"/> should yield
    /// a usable <see cref="LlamaModel"/> with the expected signature.
    /// </summary>
    // The previous `Catalog_RegisterAndResolve_YieldsLlamaModel` test
    // exercised BuiltinModels.RegisterLlama31 — that registration was
    // retired when the Llama 3.1 entry moved to a SQL-defined model
    // pair (catalog id llama-3.1-8b-instruct-gguf). Round-trip
    // coverage of the SQL → LlamaSharpSession path now lives in
    // ModelRegistrationTests / LazyModelSessions tests.
}
